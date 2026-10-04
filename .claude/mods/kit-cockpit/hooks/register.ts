import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register } from 'claude-code'

import type { Cockpit } from '../types'

// The fixed start of every deny reason from this repository's guard: MARKER in
// .agents/hooks/guard.py. check-ai-setup.py fails when the two spellings differ.
const GUARD_MARKER = 'PouryaKit guard: '
const KEEP = 'Keep blocked'
const RUN = 'Run it once'
const ACTIVE = ['pending', 'running', 'waiting']
const EMPTY: Cockpit = { agents: [], blocked: 0, overridden: 0 }
const view = atom({ plugin: 'kit-cockpit', key: 'view' } as const, EMPTY)

// A write redraws the band, so it happens only when the value changed. The band is a
// convenience: a failure here never decides a call.
async function show($: EngineInterface, change: (now: Cockpit) => Cockpit) {
  try {
    const now = await read($, view)
    if (JSON.stringify(change(now)) !== JSON.stringify(now)) await update($, view, change)
  } catch {
    // keep the last line
  }
}

async function showAgents($: EngineInterface, stopped?: string) {
  try {
    const agents = (await $.agent.list())
      .filter(agent => agent.id !== stopped && ACTIVE.includes(agent.status))
      .map(agent => agent.name ?? agent.type)
    await show($, now => ({ ...now, agents }))
  } catch {
    // keep the last line
  }
}

export const register: Register = on => {
  // tool.check's input carries no agentId, so tool.call, which does, records the Bash
  // calls the main session has in flight.
  const mainCalls = new Set<string>()

  on('tool.call', { tool: 'Bash' }, async ($, e, next) => {
    if (e.agentId !== undefined || e.tool_use_id === undefined) return next(e)
    mainCalls.add(e.tool_use_id)
    try {
      return await next(e)
    } finally {
      mainCalls.delete(e.tool_use_id)
    }
  })

  // Runs after the PreToolUse settings hooks (the guard) have decided. No .catch: a hook
  // that throws or overruns is treated as absent, so the engine's own verdict, the deny, stands.
  on('tool.check', async ($, e, next) => {
    const decided = await next(e)
    // `hook` names only the event, so the marker is what attributes a deny to the guard. Any
    // other deny (another PreToolUse hook's, a rule's, one with no reason) passes untouched.
    // The engine may put words of its own before a hook's reason, so the marker is searched for.
    const reason = decided.decision === 'deny' && decided.hook === 'PreToolUse' ? (decided.reason ?? '') : ''
    const marked = reason.indexOf(GUARD_MARKER)
    if (marked === -1 || e.tool_use_id === undefined) return decided
    await show($, now => ({ ...now, blocked: now.blocked + 1 }))
    // File-tool denies and a subagent's denies are never put to the person.
    if (e.tool !== 'Bash' || !mainCalls.has(e.tool_use_id)) return decided
    const command = String((e.input as { command?: unknown } | null)?.command ?? '')
    const shown = command.length > 160 ? command.slice(0, 160) + '…' : command
    let answer = KEEP
    try {
      answer = await $.ui.ask(`The guard blocked \`${shown}\`. ${reason.slice(marked + GUARD_MARKER.length)} Run it anyway?`, [KEEP, RUN])
    } catch {
      // dismissed, or nobody to ask (claude -p): the block stands
    }
    if (answer !== RUN) return decided
    await show($, now => ({ ...now, overridden: now.overridden + 1 }))
    return { decision: 'allow', reason: 'The user overrode the guard for this one call.' }
  })

  on('agent.spawn', async ($, e, next) => {
    const spawned = await next(e)
    await showAgents($)
    return spawned
  })

  // The stopping agent may still be listed as running at this moment, so it is left out by id.
  on('classic.SubagentStop', async ($, e, next) => {
    const stopped = await next(e)
    await showAgents($, e.agent_id)
    return stopped
  })

  on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => {
    const now = await read($, view)
    const parts = [
      now.agents.length > 0 ? `agents: ${now.agents.join(', ')}` : '',
      now.blocked > 0 ? `guard: ${now.blocked} blocked` + (now.overridden > 0 ? `, ${now.overridden} overridden` : '') : '',
    ].filter(part => part !== '')
    if (e.props.hasSurvey || parts.length === 0) return next(e)
    const { Text } = $.ui.resolve(e)
    return Text({ dimColor: true, wrap: 'truncate-end', children: parts.join(' · ') })
  })
}
