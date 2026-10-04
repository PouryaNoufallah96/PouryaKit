export const meta = {
  name: 'kit-review',
  description: 'Per-ticket dual review: kit-reviewer and a Codex inspection in parallel, then every finding checked against the code',
  whenToUse: 'The dual review in AGENTS.md workflow step 3, for a ticket that claims a runtime guarantee or changes a public API of the Kit. Pass { base, spec }; add previousFindings only to re-review after a high finding was fixed. Read-only; the main session judges the result.',
  phases: [
    { title: 'Review', detail: 'kit-reviewer and Codex (claudex-loop runner, inspect mode) review the same change independently' },
    { title: 'Verify', detail: 'merge duplicate findings and check each one against the code' },
  ],
}

const SEVERITY = ['low', 'medium', 'high']
const STATE = ['resolved', 'partial', 'unresolved']
// Item schemas are closed, so a returned item cannot carry a field the script sets itself (id, status, sources).
const FINDING = {
  type: 'object',
  additionalProperties: false,
  required: ['id', 'severity', 'path', 'evidence', 'fix'],
  properties: {
    id: { type: 'string' },
    severity: { type: 'string', enum: SEVERITY },
    path: { type: 'string', description: 'repo-relative path, with :line when known' },
    evidence: { type: 'string' },
    fix: { type: 'string' },
  },
}
const RESCORED = {
  type: 'array',
  description: 're-review only: every earlier finding, re-scored from fresh evidence',
  items: {
    type: 'object',
    additionalProperties: false,
    required: ['id', 'state', 'evidence'],
    properties: { id: { type: 'string' }, state: { type: 'string', enum: STATE }, evidence: { type: 'string' } },
  },
}
const reviewSchema = (verdicts, extra) => ({
  type: 'object',
  required: ['available', 'verdict', 'findings', 'coverage', 'limitations'],
  properties: {
    available: { type: 'boolean', description: 'false when the review did not run to completion' },
    verdict: { type: 'string', enum: verdicts },
    findings: { type: 'array', items: FINDING },
    coverage: { type: 'array', items: { type: 'string' }, description: 'files and requirements actually inspected; never empty for a completed review' },
    limitations: { type: 'array', items: { type: 'string' } },
    note: { type: 'string' },
    ...extra,
  },
})
const CLAUDE_REVIEW = reviewSchema(['block', 'fix', 'pass', 'unknown'], { rescored: RESCORED })
const CODEX_REVIEW = reviewSchema(['APPROVED', 'REVISE', 'BLOCKED', 'NONE'], {})
const VERIFIED = {
  type: 'object',
  required: ['findings'],
  properties: {
    findings: {
      type: 'array',
      items: {
        type: 'object',
        additionalProperties: false,
        required: ['sources', 'severity', 'path', 'evidence', 'fix', 'status', 'verification'],
        properties: {
          sources: { type: 'array', items: { type: 'string' }, description: 'every input id merged into this finding, for example claude:1 or codex:2' },
          severity: { type: 'string', enum: SEVERITY },
          path: { type: 'string' },
          evidence: { type: 'string' },
          fix: { type: 'string' },
          status: { type: 'string', enum: ['confirmed', 'refuted', 'unverified'] },
          verification: { type: 'string', description: 'what you opened and what it showed, with path:line' },
        },
      },
    },
  },
}

const input = typeof args === 'string'
  ? (() => { try { return JSON.parse(args) } catch { return {} } })()
  : (args ?? {})
const base = String(input.base ?? '').trim()
const spec = String(input.spec ?? '').trim()
const scope = String(input.scope ?? '').trim()
const codexEffort = String(input.codexEffort ?? 'high').trim()
const text = (value) => typeof value === 'string' && value.trim() !== ''

// base and spec are placed inside a shell command, so they are limited to plain ref and path characters.
const fail = (error) => ({ workflow: 'kit-review', complete: false, verdict: 'unknown', error })
if (!/^[A-Za-z0-9][A-Za-z0-9._\/~^-]{0,199}$/.test(base) || base.includes('..')) {
  return fail('`base` must be the pre-change commit or ref, using only letters, digits and . _ / ~ ^ -')
}
if (!/^[A-Za-z0-9_.\/][A-Za-z0-9_.\/@+-]{0,399}$/.test(spec) || spec.includes('..')) {
  return fail('`spec` must be the ticket/spec file path (absolute or repo-relative), using only letters, digits and . _ / @ + -')
}
if (!['high', 'xhigh'].includes(codexEffort)) return fail('`codexEffort` must be "high" or "xhigh".')
if (scope.length > 1000 || /[\u0000-\u001f\u007f]/.test(scope)) return fail('`scope` must be one plain-text line of at most 1000 characters.')
const previousInput = input.previousFindings ?? []
const validPrevious = Array.isArray(previousInput) && previousInput.every((f) =>
  f && typeof f === 'object' && text(f.id) && SEVERITY.includes(f.severity) && text(f.path) && text(f.evidence) && text(f.fix))
if (!validPrevious || new Set(previousInput.map((f) => f.id)).size !== previousInput.length) {
  return fail('`previousFindings` must be the `findings` array of the previous kit-review result: objects with a unique `id`, `severity`, `path`, `evidence` and `fix`.')
}
const previous = previousInput.map(({ id, severity, path, evidence, fix }) => ({ id, severity, path, evidence, fix }))
if (JSON.stringify(previous).length > 20000) return fail('`previousFindings` is too long; pass only the findings to re-score.')

const reviewerPrompt = `Review the working tree against base ${base} for the ticket/spec at ${spec}. Run \`git diff ${base}\` and \`git status --porcelain\`, and open every added or untracked file.${scope ? `\nScope: ${scope}` : ''}
${previous.length ? `\nThis is a re-review. Re-score these earlier findings from fresh evidence; do not start a new hunt:\n${JSON.stringify(previous, null, 2)}\nPut every earlier finding in \`rescored\` exactly once, with its original id and a state of resolved, partial or unresolved. Repeat each partial or unresolved one in \`findings\` under its original id, and add any regression a fix introduced as a new finding with a new id.\n` : ''}
Return the structured review: available true, verdict set to your derived disposition (block, fix, pass or unknown), your findings, every file you opened as coverage, and your limitations. If you could not read the spec or the diff, return available false, verdict unknown and a note.`

const runnerCommand = `python3 .agents/skills/claudex-loop/scripts/runner.py inspect --host claude --builder claude --repo . --plan '${spec}' --base '${base}' --model gpt-6-astra --effort ${codexEffort} --timeout 570`
const relayPrompt = `You relay an independent Codex inspection. You do not review code yourself, and you never add, drop or reword a finding.

1. From the repository root, run exactly this command with the Bash tool, in the foreground, with the tool timeout set to 600000 ms. Add no flags, pipes or redirects:
\`\`\`bash
${runnerCommand}
\`\`\`
   Its first stdout line is JSON naming an \`artifacts\` directory. It exits 0 only when the inspection completed.
2. Read \`<artifacts>/result.json\`.
3. If its \`status\` is "completed", return available true, verdict set to \`response.verdict\`, and \`response.findings\`, \`response.coverage\` and \`response.limitations\` copied verbatim.
4. In every other case (non-zero exit, timeout, no artifacts line, no result.json, status "failed") return available false, verdict NONE, empty findings, coverage and limitations, and a note quoting the \`error\` field or the stderr line.`

phase('Review')
const [claudeRaw, codexRaw] = await parallel([
  () => agent(reviewerPrompt, { label: 'review:kit-reviewer', phase: 'Review', agentType: 'kit-reviewer', schema: CLAUDE_REVIEW }),
  () => agent(relayPrompt, { label: 'review:codex', phase: 'Review', model: 'sonnet', effort: 'low', schema: CODEX_REVIEW }),
])
const missing = (note) => ({ available: false, verdict: null, findings: [], coverage: [], limitations: [], note })
const claude = claudeRaw ?? missing('kit-reviewer did not return')
const codex = codexRaw ?? missing('the Codex relay did not return')
const list = (value) => (Array.isArray(value) ? value : [])

// A leg counts as a review only when it says it ran, names what it read, and its verdict fits its findings.
// kit-reviewer's "unknown" (part of the diff unread) and Codex's BLOCKED (required evidence not inspected) do not count.
const gap = (review, reviewed, hasOpen, cleanVerdict) =>
  review.available !== true ? (review.note ?? 'the review did not run to completion')
  : !reviewed.includes(review.verdict) ? `verdict "${review.verdict}" is not a completed review`
  : !list(review.coverage).length ? 'no coverage listed'
  : hasOpen === (review.verdict === cleanVerdict) ? `verdict "${review.verdict}" contradicts its findings`
  : null
const claudeOpen = list(claude.findings).length > 0 || list(claude.rescored).some((r) => r.state !== 'resolved')
// The runner lets APPROVED carry low findings, never high or medium ones.
const codexOpen = codex.verdict === 'APPROVED'
  ? list(codex.findings).some((f) => f.severity !== 'low')
  : list(codex.findings).length > 0
const claudeGap = gap(claude, ['block', 'fix', 'pass'], claudeOpen, 'pass')
const codexGap = gap(codex, ['APPROVED', 'REVISE'], codexOpen, 'APPROVED')
if (claudeGap) log(`kit-reviewer leg incomplete: ${claudeGap}`)
if (codexGap) log(`Codex leg incomplete: ${codexGap}`)

// Findings are tagged by position, so two findings a reviewer gave the same id stay two findings.
const tag = (who, review) => list(review.findings).map((f, i) => ({ ...f, id: `${who}:${i + 1}`, reviewerId: f.id ?? null }))
const raised = [...tag('claude', claude), ...tag('codex', codex)]
const byId = new Map(raised.map((f) => [f.id, f]))
const rank = (severity) => SEVERITY.indexOf(severity)
const higher = (a, b) => (rank(a) >= rank(b) ? a : b)

let findings = []
let verifyReturned = true
if (raised.length) {
  phase('Verify')
  const verifyPrompt = `You check review findings against the code. Read-only: do not edit, stage or run anything that changes the tree.

Two independent reviewers examined the working tree against base ${base} for the ticket/spec at ${spec}. Their findings, each with an id:
${JSON.stringify(raised, null, 2)}

1. Merge findings that name the same file and the same root cause, and list every merged id in \`sources\`. Every input id appears in exactly one output finding. Add no findings of your own.
2. For each one, open the cited code and the spec, ADR or glossary entry it relies on, then set \`status\`:
   - confirmed: the code is as described, and the defect is real and introduced by this change;
   - refuted: the code shows the claim is wrong, or the issue predates ${base};
   - unverified: you could not establish either. Unverified is not clean.
3. \`verification\` states what you opened and what it showed, with path:line. Keep the highest severity any source gave unless the evidence supports a higher one.`
  // agent() throws outside parallel(), for example when its output never fits the schema. That counts as
  // "verification did not return": the raised findings fall through below as unverified.
  let result = null
  try {
    result = await agent(verifyPrompt, { label: 'verify', phase: 'Verify', effort: 'high', schema: VERIFIED })
  } catch (error) {
    log(`Verification stage failed: ${error?.message ?? error}`)
  }
  verifyReturned = !!result
  const accounted = new Set()
  findings = list(result?.findings).map((v) => {
    // A source id counts once: a finding the verifier lists twice stays in the first place it appears.
    const sources = [...new Set(list(v.sources))].filter((id) => byId.has(id) && !accounted.has(id))
    sources.forEach((id) => accounted.add(id))
    // Severity never drops below what a reviewer claimed; the main session decides any downgrade.
    const severity = sources.reduce((top, id) => higher(top, byId.get(id).severity), v.severity)
    const raisedBy = [...new Set(sources.map((id) => id.split(':')[0]))]
    return { ...v, sources, reviewerIds: sources.map((id) => byId.get(id).reviewerId), severity, raisedBy: raisedBy.length ? raisedBy : ['verifier'] }
  })
  for (const f of raised) {
    if (accounted.has(f.id)) continue
    const { id, reviewerId, ...rest } = f
    findings.push({ ...rest, sources: [id], reviewerIds: [reviewerId], raisedBy: [id.split(':')[0]], status: 'unverified', verification: 'The verification stage did not account for this finding.' })
  }
}

// Re-review: every earlier finding is reconciled against the reviewer's re-score. One that is missing counts as
// unresolved, and each partial or unresolved one still stands at no less than its earlier severity.
const repeated = new Set(list(claude.findings).map((f) => f.id))
const rescored = previous.map((p) => {
  const worst = list(claude.rescored).filter((r) => r.id === p.id).sort((a, b) => STATE.indexOf(b.state) - STATE.indexOf(a.state))[0]
  if (!worst) return { id: p.id, state: 'unresolved', evidence: 'The reviewer did not re-score this finding; it counts as unresolved.' }
  if (worst.state === 'resolved' && repeated.has(p.id)) return { id: p.id, state: 'unresolved', evidence: `Re-scored resolved but repeated as a finding: ${worst.evidence}` }
  return { id: p.id, state: worst.state, evidence: worst.evidence }
})
const carried = new Map()
for (const r of rescored) {
  if (r.state === 'resolved') continue
  const earlier = previous.find((p) => p.id === r.id)
  const current = findings.find((f) => !carried.has(f) && f.sources.some((id) => id.startsWith('claude:') && byId.get(id).reviewerId === r.id))
  if (!current) {
    const added = { ...earlier, sources: [], reviewerIds: [r.id], raisedBy: ['previous'], status: 'unverified', verification: `Carried from the previous round, re-scored ${r.state}: ${r.evidence}`, previous: r }
    findings.push(added)
    carried.set(added, r.id)
    continue
  }
  current.severity = higher(current.severity, earlier.severity)
  current.previous = r
  if (current.status === 'refuted') {
    current.status = 'unverified'
    current.verification = `The reviewer re-scored this earlier finding ${r.state}; the verification stage refuted it. Disputed, so it stands: ${current.verification}`
  }
  carried.set(current, r.id)
}

// Stable ids: a carried finding keeps its earlier id, every other finding gets the next free F-number.
// The id is set last, so nothing a reviewer or the verifier returned can replace it.
const used = new Set(previous.map((p) => p.id))
let next = previous.reduce((top, p) => Math.max(top, /^F(\d+)$/.test(p.id) ? Number(p.id.slice(1)) : 0), 0)
findings = findings.map((f) => {
  let id = carried.get(f)
  if (!id) {
    do { next += 1 } while (used.has(`F${next}`))
    id = `F${next}`
  }
  return { ...f, id }
})

const complete = !claudeGap && !codexGap && verifyReturned
const standing = findings.filter((f) => f.status !== 'refuted')
const verdict = !complete ? 'unknown'
  : standing.some((f) => f.severity === 'high') ? 'block'
  : standing.length ? 'fix'
  : 'pass'
const leg = (review, problem) => ({
  available: !problem,
  verdict: review.verdict ?? null,
  findings: list(review.findings).length,
  coverage: list(review.coverage),
  limitations: list(review.limitations),
  note: problem ?? review.note ?? null,
})
const count = (status) => findings.filter((f) => f.status === status).length

return {
  workflow: 'kit-review',
  complete,
  verdict,
  base,
  spec,
  reviewers: { claude: leg(claude, claudeGap), codex: { ...leg(codex, codexGap), effort: codexEffort } },
  counts: { confirmed: count('confirmed'), unverified: count('unverified'), refuted: count('refuted') },
  findings,
  rescored,
  note: 'Read-only: nothing was edited. The main session judges these findings, decides fix or done, and re-runs kit-review after fixes with previousFindings set to this findings array. A verdict of "unknown" means a reviewer or the verification stage did not return a usable review; it is not a pass.',
}
