import { expect, test } from 'claude-code/testing'

// The guard starts every deny reason with its marker; the mod acts on no other deny.
const GUARD_DENY = { decision: 'deny', reason: 'PouryaKit guard: Recursive delete is blocked.', hook: 'PreToolUse' }
const BAND = {
  plugin: 'kit-cockpit',
  surface: 'terminal',
  component: 'AbovePrompt',
  viewport: { columns: 100, rows: 30 },
  props: { hasSurvey: false, isWorking: false, maxRows: 5, bodyColumns: 90, scroll: { offset: 0, bodyRows: 5 }, view: {} },
} as const

type Scene = {
  engineSays: object
  userSays?: string // absent: the person dismisses the question
  tool?: string
  input?: Record<string, unknown>
  agentId?: string
}

// Stands for Claude Code beneath the mod. A test registers its hooks before its first call
// on $, so one world is built up front and each decision sets the scene it plays.
function world(on: any) {
  const w = { scene: undefined as Scene | undefined, asked: 0, question: '', calls: 0, gate: Promise.resolve() }
  on('tool.check', () => w.scene?.engineSays)
  on('tool.call', async (_: unknown, e: any) => {
    if (e.tool === 'AskUserQuestion') {
      w.asked += 1
      w.question = e.questions[0].question
      if (w.scene?.userSays === undefined) return { deny: 'The user dismissed the question.' }
      return { result: { answers: { [w.question]: w.scene.userSays } } }
    }
    await w.gate
    return { result: 'ran' }
  })
  on('ui.render', ($: any, e: any) => $.ui.resolve(e).Box({})) // the engine's own, empty band
  on('classic.SubagentStop', () => ({}))
  return w
}

// Starts a tool call and, while it is in flight (the engine decides tool.check before the
// tool runs), asks tool.check about it.
async function decide($: any, w: ReturnType<typeof world>, scene: Scene) {
  const tool = scene.tool ?? 'Bash'
  const input = scene.input ?? { command: 'git clean -fd' }
  const id = 'toolu_' + (w.calls += 1)
  let release = () => {}
  Object.assign(w, { scene, asked: 0, question: '', gate: new Promise<void>(resolve => (release = resolve)) })
  const call = $.tool.call({ tool, ...input, tool_use_id: id, ...(scene.agentId ? { agentId: scene.agentId } : {}) })
  const verdict = await $.tool.check({ tool, input, tool_use_id: id })
  release()
  await call
  return verdict
}

async function band($: any, props: object = {}) {
  const ui = await $.ui.mount({ ...BAND, props: { ...BAND.props, ...props } })
  const text = (await ui.find({ type: 'Text' }))?.text
  await ui.unmount()
  return text
}

test('keep blocked: the deny stands and the question quotes the reason and the command', async ($, on) => {
  const w = world(on)
  expect(await decide($, w, { engineSays: GUARD_DENY, userSays: 'Keep blocked' })).toEqual(GUARD_DENY)
  expect(w.asked).toBe(1)
  expect(w.question).toMatch(/git clean -fd/)
  expect(w.question).toMatch(/Recursive delete is blocked/)
  expect(w.question).not.toMatch(/PouryaKit guard:/)
})

test("the guard's deny is recognised behind words the engine puts first", async ($, on) => {
  const w = world(on)
  const prefixed = { ...GUARD_DENY, reason: 'PreToolUse:Bash hook error: ' + GUARD_DENY.reason }
  expect(await decide($, w, { engineSays: prefixed, userSays: 'Keep blocked' })).toEqual(prefixed)
  expect(w.asked).toBe(1)
  expect(w.question).not.toMatch(/hook error|PouryaKit guard:/)
})

test('run it once: the call is allowed', async ($, on) => {
  const w = world(on)
  const verdict = await decide($, w, { engineSays: GUARD_DENY, userSays: 'Run it once' })
  expect(w.asked).toBe(1)
  expect(verdict.decision).toBe('allow')
})

test('a dismissed question keeps the deny', async ($, on) => {
  const w = world(on)
  expect(await decide($, w, { engineSays: GUARD_DENY })).toEqual(GUARD_DENY)
  expect(w.asked).toBe(1)
})

test('a long command is cut in the question', async ($, on) => {
  const w = world(on)
  await decide($, w, { engineSays: GUARD_DENY, userSays: 'Keep blocked', input: { command: 'rm -rf ' + 'x'.repeat(400) } })
  expect(w.question.length).toBeLessThan(330)
})

test("a subagent's denied call is not put to the person", async ($, on) => {
  const w = world(on)
  expect(await decide($, w, { engineSays: GUARD_DENY, userSays: 'Run it once', agentId: 'agent_1' })).toEqual(GUARD_DENY)
  expect(w.asked).toBe(0)
})

test('a denied file-tool call is not put to the person', async ($, on) => {
  const w = world(on)
  const scene = { engineSays: GUARD_DENY, userSays: 'Run it once', tool: 'Read', input: { file_path: '.env' } }
  expect(await decide($, w, scene)).toEqual(GUARD_DENY)
  expect(w.asked).toBe(0)
})

test('a deny that no PreToolUse hook made is not put to the person, nor counted', async ($, on) => {
  const w = world(on)
  const rule = { decision: 'deny', reason: 'deny rule', rule: 'Bash(git clean *)' }
  expect(await decide($, w, { engineSays: rule, userSays: 'Run it once' })).toEqual(rule)
  expect(w.asked).toBe(0)
  expect(await band($)).toBeUndefined()
})

test("another PreToolUse hook's deny is not put to the person, nor counted", async ($, on) => {
  const w = world(on)
  const other = { decision: 'deny', reason: 'Blocked by a user-level hook.', hook: 'PreToolUse' }
  expect(await decide($, w, { engineSays: other, userSays: 'Run it once' })).toEqual(other)
  const silent = { decision: 'deny', hook: 'PreToolUse' }
  expect(await decide($, w, { engineSays: silent, userSays: 'Run it once' })).toEqual(silent)
  expect(w.asked).toBe(0)
  expect(await band($)).toBeUndefined()
})

test('an allowed call passes untouched', async ($, on) => {
  const w = world(on)
  expect(await decide($, w, { engineSays: { decision: 'allow' }, userSays: 'Run it once' })).toEqual({ decision: 'allow' })
  expect(w.asked).toBe(0)
})

test('the band draws nothing when there is nothing to show', async ($, on) => {
  world(on)
  expect(await band($)).toBeUndefined()
})

test('the band counts blocks and overrides', async ($, on) => {
  const w = world(on)
  await decide($, w, { engineSays: GUARD_DENY, userSays: 'Keep blocked' })
  expect(await band($)).toBe('guard: 1 blocked')
  await decide($, w, { engineSays: GUARD_DENY, userSays: 'Run it once' })
  expect(await band($)).toBe('guard: 2 blocked, 1 overridden')
})

test("the band counts a subagent's block", async ($, on) => {
  const w = world(on)
  await decide($, w, { engineSays: GUARD_DENY, agentId: 'agent_1' })
  expect(await band($)).toBe('guard: 1 blocked')
})

test('the band yields to a survey', async ($, on) => {
  const w = world(on)
  await decide($, w, { engineSays: GUARD_DENY, userSays: 'Keep blocked' })
  expect(await band($, { hasSurvey: true })).toBeUndefined()
})

test('the band lists running agents and drops one that stopped', async ($, on) => {
  world(on)
  const worker = { id: 'a1', description: 'ticket 12', type: 'kit-implementer', status: 'running' } as const
  const idle = { id: 'a2', description: 'waiting', type: 'kit-scout', status: 'idle' } as const
  on('agent.list', () => ({ value: [worker, idle] }))
  on('agent.spawn', () => ({ model: 'opus', agentId: 'a1' }))
  await $.agent.spawn({
    tool_use_id: 'toolu_agent',
    prompt: 'Build ticket 12',
    description: 'ticket 12',
    subagentType: 'kit-implementer',
    provider: { plugin: 'engine', tier: 'core' },
    parentModel: 'opus',
    background: true,
    fork: false,
  })
  expect(await band($)).toBe('agents: kit-implementer')
  await $.classic.SubagentStop({ stop_hook_active: false, agent_id: 'a1', agent_transcript_path: '/tmp/a1.jsonl', agent_type: 'kit-implementer' })
  expect(await band($)).toBeUndefined()
})
