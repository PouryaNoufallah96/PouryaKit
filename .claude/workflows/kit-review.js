export const meta = {
  name: 'kit-review',
  description: 'Per-ticket dual review: a Codex inspection, then kit-reviewer, on a worktree of one commit, then every finding checked against the code',
  whenToUse: 'The dual review in AGENTS.md workflow step 3, for a ticket that claims a Kit runtime guarantee or changes a public API of the Kit. Pass { base, ticket } (or { base, spec } for work with no ticket); add previous (the earlier result file) only to re-review after a high finding was fixed. Read-only; returns a short digest and names the file with the full result; the main session judges it.',
  phases: [
    { title: 'Prepare', detail: 'resolve the commits, fetch the ticket, check out a detached review worktree' },
    { title: 'Review', detail: 'Codex (claudex-loop runner, inspect mode; over 900,000 bytes of diff the leg is skipped with that reason), then kit-reviewer, review the same commit independently' },
    { title: 'Verify', detail: 'merge duplicate findings and check each one against the code' },
    { title: 'Finish', detail: 'write the full result file and remove the review worktree' },
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
// A leg reports how much of the diff it read, never the list of files it opened.
const reviewSchema = (verdicts, extra) => ({
  type: 'object',
  required: ['available', 'verdict', 'findings', 'read_count', 'unread', 'limitations'],
  properties: {
    available: { type: 'boolean', description: 'false when the review did not run to completion' },
    verdict: { type: 'string', enum: verdicts },
    findings: { type: 'array', items: FINDING },
    read_count: { type: 'integer', minimum: 0, description: 'how many changed files were opened' },
    unread: { type: 'array', items: { type: 'string' }, description: 'parts of the diff not read; empty when all of it was read' },
    limitations: { type: 'array', items: { type: 'string' } },
    note: { type: 'string' },
    ...extra,
  },
})
const CLAUDE_REVIEW = reviewSchema(['block', 'fix', 'pass', 'unknown'], { rescored: RESCORED })
const CODEX_REVIEW = reviewSchema(['APPROVED', 'REVISE', 'BLOCKED', 'NONE'], { error: { type: 'string' } })
const PREPARED = {
  type: 'object',
  required: ['ok'],
  properties: {
    ok: { type: 'boolean' },
    error: { type: 'string' },
    top: { type: 'string' },
    base: { type: 'string' },
    head: { type: 'string' },
    worktree: { type: 'string' },
    run: { type: 'string' },
    previousBase: { type: 'string' },
    previousCount: { type: 'integer', minimum: 0 },
    lastId: { type: 'integer', minimum: 0 },
    previous: { type: 'array', items: FINDING },
  },
}
const VERIFIED = {
  type: 'object',
  required: ['findings'],
  properties: {
    findings: {
      type: 'array',
      items: {
        type: 'object',
        additionalProperties: false,
        required: ['sources', 'severity', 'path', 'claim', 'evidence', 'fix', 'status', 'verification'],
        properties: {
          sources: { type: 'array', items: { type: 'string' }, description: 'every input id merged into this finding, for example claude:1 or codex:2' },
          severity: { type: 'string', enum: SEVERITY },
          path: { type: 'string' },
          claim: { type: 'string', description: 'the defect in one line of at most 160 characters' },
          evidence: { type: 'string' },
          fix: { type: 'string' },
          status: { type: 'string', enum: ['confirmed', 'refuted', 'unverified'] },
          verification: { type: 'string', description: 'what you opened and what it showed, with path:line' },
        },
      },
    },
  },
}
const FINISHED = {
  type: 'object',
  required: ['verdict', 'bytes', 'removed'],
  properties: { verdict: { type: 'string' }, bytes: { type: 'integer' }, removed: { type: 'boolean' }, error: { type: 'string' } },
}

// `args` is a JSON object, or the word form the docs show: `base X ticket N`, optionally joined by `with`, `and` or commas.
const WORD_KEYS = ['base', 'ticket', 'spec', 'head', 'previous', 'rescore', 'evidence', 'codexModel', 'codexEffort']
const parseWords = (text) => {
  const out = {}
  const words = text.trim().split(/\s+/).map((w) => w.replace(/,$/, '')).filter((w) => w && !['with', 'and'].includes(w))
  for (let i = 0; i < words.length; i += 2) {
    const key = words[i].replace(/^--/, '')
    if (!WORD_KEYS.includes(key) || i + 1 >= words.length) return null
    out[key] = words[i + 1]
  }
  return Object.keys(out).length ? out : null
}
const input = typeof args === 'string'
  ? (() => { try { return JSON.parse(args) } catch { return parseWords(args) ?? {} } })()
  : (args ?? {})
const str = (value) => String(value ?? '').trim()
const base = str(input.base)
const ticket = str(input.ticket).replace(/^#/, '')
const spec = str(input.spec)
const headInput = str(input.head) || 'HEAD'
const scope = str(input.scope)
const codexModel = str(input.codexModel)
const codexEffort = str(input.codexEffort)
const previousFile = str(input.previous)
const evidenceFile = str(input.evidence)
const rescoreIds = (Array.isArray(input.rescore) ? input.rescore : str(input.rescore).split(',')).map(str).filter(Boolean)
const list = (value) => (Array.isArray(value) ? value : [])

// Every value below is placed inside a shell command, so each is limited to plain ref, path or id characters.
const fail = (error) => ({ workflow: 'kit-review', complete: false, verdict: 'unknown', error })
const REF = /^[A-Za-z0-9][A-Za-z0-9._\/~^-]{0,199}$/
const PATH = /^[A-Za-z0-9_.\/~][A-Za-z0-9_.\/@+-]{0,399}$/
const USAGE = 'Pass { base, ticket } for a ticket, or { base, spec } for work with no ticket; head (the commit of the implementer; required in practice, since the default is the HEAD of the checkout and a head equal to base is refused), scope, evidence (a file of recorded execution evidence), codexModel, codexEffort, and for a re-review previous (the earlier result file) with optional rescore ids. JSON or the word form `base X ticket N`.'
for (const key of Object.keys(input)) {
  if (!['base', 'ticket', 'spec', 'head', 'scope', 'codexModel', 'codexEffort', 'previous', 'rescore', 'evidence'].includes(key)) return fail(`Unknown argument \`${key}\`. ${USAGE}`)
}
if (!REF.test(base) || base.includes('..')) return fail(`\`base\` must be the pre-change commit or ref, using only letters, digits and . _ / ~ ^ -. ${USAGE}`)
if (!!ticket === !!spec) return fail(`Pass exactly one of \`ticket\` (an issue number) and \`spec\` (a file path). ${USAGE}`)
if (ticket && !/^[1-9][0-9]{0,6}$/.test(ticket)) return fail('`ticket` must be an issue number, for example 42.')
if (spec && (!PATH.test(spec) || spec.includes('..'))) return fail('`spec` must be a file path (absolute, or relative to the repository root) using only letters, digits and . _ / @ + -')
if (!REF.test(headInput) || headInput.includes('..')) return fail('`head` must be a commit or ref, using only letters, digits and . _ / ~ ^ -')
if (codexModel && !/^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$/.test(codexModel)) return fail('`codexModel` must use only letters, digits and . _ -')
if (codexEffort && !['high', 'xhigh'].includes(codexEffort)) return fail('`codexEffort` must be "high" or "xhigh".')
if (scope.length > 1000 || /[\u0000-\u001f\u007f]/.test(scope)) return fail('`scope` must be one plain-text line of at most 1000 characters.')
if (previousFile && (!PATH.test(previousFile) || previousFile.includes('..'))) return fail('`previous` must be the path of an earlier kit-review result file.')
if (evidenceFile && (!PATH.test(evidenceFile) || evidenceFile.includes('..'))) return fail('`evidence` must be the path of a text file holding the recorded execution evidence.')
if (rescoreIds.length && !previousFile) return fail('`rescore` needs `previous`, the earlier kit-review result file that holds those findings.')
if (rescoreIds.some((id) => !/^[A-Za-z0-9_-]{1,20}$/.test(id))) return fail('`rescore` must list finding ids such as F1, F3.')

// Prepare: the script cannot run git or read files, so one agent runs a fixed command. The worktree is created last,
// so a failure before it leaves nothing to clean up.
const extractPrevious = `import json, sys
path, ids = sys.argv[1], [i for i in sys.argv[2].split(",") if i]
r = json.load(open(path, encoding="utf-8"))
if r.get("workflow") != "kit-review" or not isinstance(r.get("findings"), list):
    sys.exit("kit-review: " + path + " is not a kit-review result file")
by = {f["id"]: f for f in r["findings"]}
missing = [i for i in ids if i not in by]
if missing:
    sys.exit("kit-review: no finding " + ", ".join(missing) + " in " + path)
pick = [by[i] for i in ids] if ids else [f for f in r["findings"] if f.get("status") != "refuted"]
keys = ("id", "severity", "path", "evidence", "fix")
numbers = [int(x["id"][1:]) for x in r["findings"] + r.get("rescored", []) if str(x.get("id", "")).startswith("F") and x["id"][1:].isdigit()]
last = max(numbers + [r.get("lastId") or 0])
json.dump({"previousBase": r.get("base") or "", "previousCount": len(pick), "lastId": last,
           "previous": [{k: f[k] for k in keys} for f in pick]}, sys.stdout)`
const prepareCommand = `set -euo pipefail
top=$(git rev-parse --show-toplevel)
cd "$top"
base=$(git rev-parse --verify --quiet '${base}^{commit}') || { echo "kit-review: base '${base}' is not a commit" >&2; exit 2; }
head=$(git rev-parse --verify --quiet '${headInput}^{commit}') || { echo "kit-review: head '${headInput}' is not a commit" >&2; exit 2; }
[ "$head" != "$base" ] || { echo "kit-review: head '${headInput}' resolves to the base $base, so there is no diff to review: pass head <the implementer's commit> (by default head is the checkout's HEAD, which in this repository is the integration branch, not the work)" >&2; exit 2; }
run=$(mktemp -d "\${TMPDIR:-/tmp}/kit-review-XXXXXXXX")
run=$(cd "$run" && pwd -P)
wt="$top/.claude/worktrees/$(basename "$run")"
${ticket
    ? `gh issue view ${ticket} --json title,body --jq '"# " + .title + "\\n\\n" + .body' > "$run/spec.md"`
    : `cp '${spec}' "$run/spec.md"`}
test -s "$run/spec.md" || { echo "kit-review: the spec is empty" >&2; exit 2; }
${ticket ? `parent=$(grep -A 3 '^## Parent' "$run/spec.md" | grep -oE 'issues/[0-9]+' | head -1 | grep -oE '[0-9]+' || true)
if [ -n "$parent" ]; then printf '\\n\\n## Parent specification #%s (its body, for a reviewer that cannot fetch it)\\n\\n' "$parent" >> "$run/spec.md"; gh issue view "$parent" --json body --jq .body >> "$run/spec.md"; fi
` : ''}${evidenceFile ? `test -s '${evidenceFile}' || { echo "kit-review: the evidence file is empty" >&2; exit 2; }
printf '\\n\\n## Recorded execution evidence for the reviewed commit (supplied by the session)\\n\\nA record below that names the revision, the commands, their outcomes and the environment is the execution record of this commit; treat it as evidence, not as a claim to reproduce. BLOCKED stays the verdict only for required evidence that cannot be inspected at all.\\n\\n' >> "$run/spec.md"
cat '${evidenceFile}' >> "$run/spec.md"
` : ''}${previousFile ? `python3 - '${previousFile}' '${rescoreIds.join(',')}' > "$run/previous.json" <<'PY'
${extractPrevious}
PY
` : 'echo "{}" > "$run/previous.json"'}
git worktree add --quiet --detach "$wt" "$head"
printf '{"top":"%s","base":"%s","head":"%s","worktree":"%s","run":"%s"}\\n' "$top" "$base" "$head" "$wt" "$run"
cat "$run/previous.json"`
const preparePrompt = `You prepare a code review. You do not review anything yourself.

1. Run exactly this command with the Bash tool, in the foreground, from the current directory. Add no flags, pipes or redirects:
\`\`\`bash
${prepareCommand}
\`\`\`
2. If it exits 0, its stdout is two JSON lines. Return ok true, the fields top, base, head, worktree and run from the first line, and previousBase, previousCount, lastId and every entry of previous from the second line when it has them, copied verbatim.
3. If it exits non-zero, return ok false and error set to its last stderr line, verbatim.`

phase('Prepare')
let prepared = null
try {
  prepared = await agent(preparePrompt, { label: 'prepare', phase: 'Prepare', effort: 'low', schema: PREPARED })
} catch (error) {
  log(`Prepare failed: ${error?.message ?? error}`)
}
// The prepare agent may have created the worktree before it failed, so every early exit says how to remove it.
const leftover = (worktree) => `Remove the review worktree it may have left with \`git worktree remove --force ${typeof worktree === 'string' && worktree ? worktree : '.claude/worktrees/kit-review-<random>'}\` (\`git worktree list\` shows it).`
if (!prepared?.ok) return fail(`Could not prepare the review: ${prepared?.error ?? 'the prepare agent did not return'}. ${leftover(prepared?.worktree)}`)
const { top, worktree, run } = prepared
const absolute = (p) => typeof p === 'string' && /^\/[^'\u0000-\u001f\u007f]{1,500}$/.test(p)
if (![top, worktree, run].every(absolute) || !/\/\.claude\/worktrees\/kit-review-[^/]+$/.test(worktree)
  || !/^[0-9a-f]{40}$/.test(prepared.base ?? '') || !/^[0-9a-f]{40}$/.test(prepared.head ?? '')) {
  return fail(`The prepare agent returned an unexpected result. ${leftover(absolute(worktree) ? worktree : null)} ${JSON.stringify(prepared).slice(0, 400)}`)
}
const head = prepared.head
const baseCommit = prepared.base
const specFile = `${run}/spec.md`
const resultFile = `${run}/result.json`
// The run directory is in system temp, which a later session cannot rely on, so the result is also kept, gitignored,
// under the repository root: one file per ticket or spec and reviewed commit.
const reviewDir = `${top}/.claude/reviews`
const reviewFile = `${reviewDir}/${ticket || spec.split('/').pop().replace(/\.[^.]+$/, '') || 'spec'}-${head.slice(0, 7)}.json`
const previous = list(prepared.previous)
// A re-review that lost earlier findings on the way would pass them unseen, so the list must match what the
// extraction counted, and the ids asked for. The worktree exists by now, so the finish step below still removes it.
const ids = previous.map((p) => p.id)
const lostPrevious = !previousFile ? null
  : !Number.isInteger(prepared.previousCount) || !Number.isInteger(prepared.lastId) ? 'the prepare agent did not report previousCount and lastId'
  : previous.length !== prepared.previousCount ? `the prepare agent returned ${previous.length} of the ${prepared.previousCount} earlier findings`
  : new Set(ids).size !== ids.length ? 'the earlier findings repeat an id'
  : rescoreIds.length && (ids.length !== rescoreIds.length || rescoreIds.some((id) => !ids.includes(id))) ? `the earlier findings returned (${ids.join(', ')}) are not the ids asked for (${rescoreIds.join(', ')})`
  : null
if (previousFile && !lostPrevious && !previous.length) log('The earlier result file names no finding to re-score; this runs as a first review.')
if (previousFile && prepared.previousBase && !baseCommit.startsWith(prepared.previousBase) && !prepared.previousBase.startsWith(baseCommit)) {
  log(`The earlier result was reviewed against base ${prepared.previousBase}, this run against ${baseCommit}.`)
}
const source = ticket ? `ticket #${ticket}` : `spec ${spec}`

let full = null
let finished = null
let saved = false
try {
  if (lostPrevious) throw new Error(`Re-review stopped before reviewing: ${lostPrevious}. Run it again.`)
  const reviewerPrompt = `Review commit ${head} against base ${baseCommit} for ${source}; its text is in ${specFile}.
The commit is checked out, detached and clean, in the review worktree ${worktree}. Work only there: run \`git -C ${worktree} diff --stat ${baseCommit} HEAD\` and \`git -C ${worktree} diff ${baseCommit} HEAD\`, and open each changed file by its absolute path under ${worktree}. Never read the diff from any other checkout.
You may build and run tests in the review worktree (dotnet build, dotnet test); their output lands in gitignored folders. Edit, create, stage or delete nothing there that git would see: the Codex leg fingerprints the same worktree and fails if it changes.${scope ? `\nScope: ${scope}` : ''}
${previous.length ? `\nThis is a re-review. Re-score these earlier findings from fresh evidence; do not start a new hunt:\n${JSON.stringify(previous, null, 2)}\nPut every earlier finding in \`rescored\` exactly once, with its original id and a state of resolved, partial or unresolved. Repeat each partial or unresolved one in \`findings\` under its original id, and add any regression a fix introduced as a new finding with a new id.\n` : ''}
Return the structured review: available true, verdict set to your derived disposition (block, fix, pass or unknown), your findings, read_count (how many changed files you opened), unread (the parts of the diff you did not read; empty when you read all of it, and verdict unknown when it is not), and your limitations. If you could not read the spec or the diff, return available false, verdict unknown and a note.`

  // The runner's coverage is free text, so the Codex leg reports it as a count: read_count is the number of coverage
  // entries the runner returned, and unread carries the limitations of a BLOCKED review. A failed run reports the
  // runner's own reason: Codex's error events in stdout.txt, then the last stderr line, then the record's error. A
  // runner that fails before it starts Codex (CLI not on PATH, version probe) prints no artifacts line, only its
  // indented record, so then the whole stdout is read as the record.
  const summarizeCodex = `import json, pathlib, sys
run = pathlib.Path(sys.argv[1])
def text(p):
    try:
        return p.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return ""
def parse(s):
    try:
        value = json.loads(s)
    except ValueError:
        return {}
    return value if isinstance(value, dict) else {}
out = {"available": False, "verdict": "NONE", "findings": [], "read_count": 0, "unread": [], "limitations": [], "error": ""}
stdout = text(run / "codex.out")
first = parse((stdout.splitlines() or [""])[0])
art = pathlib.Path(first["artifacts"]) if isinstance(first.get("artifacts"), str) else None
record = parse(text(art / "result.json")) if art else parse(stdout)
if record.get("status") == "completed":
    r = record["response"]
    out.update(available=True, verdict=r["verdict"], findings=r["findings"], limitations=r["limitations"],
               read_count=len(r["coverage"]), unread=r["limitations"] if r["verdict"] == "BLOCKED" else [])
else:
    why = []
    for line in text(art / "stdout.txt").splitlines() if art else []:
        try:
            e = json.loads(line)
        except ValueError:
            continue
        err = e.get("error") if isinstance(e, dict) else None
        m = e.get("message") if isinstance(e, dict) and e.get("type") == "error" else err.get("message") if isinstance(err, dict) else None
        if isinstance(m, str) and m and m not in why:
            why.append(m)
    if not why and art:
        why += [l.strip() for l in text(art / "stderr.txt").splitlines() if l.strip()][-1:]
    why += [l.strip() for l in text(run / "codex.err").splitlines() if l.strip()][-1:]
    if record.get("error"):
        why.append("runner: " + record["error"])
    out["error"] = " | ".join(why)[:600] or "the runner left no result.json and no error message"
print(json.dumps(out, ensure_ascii=False))`
  // Inspect mode puts the whole diff in Codex's prompt, and Codex refuses an input over 1,048,576 characters in about
  // two seconds with no usable reason (2.68 MB, 2026-10-08). Over 900,000 bytes, which leaves room for the prompt
  // around the diff, the leg is skipped with an error that gives the size and the hand rerun in review mode.
  const runnerCommand = `size=$(git -C '${worktree}' diff --no-ext-diff --no-textconv '${baseCommit}' HEAD | wc -c | tr -d ' ')
if [ "$size" -gt 900000 ]; then
printf '{"available": false, "verdict": "NONE", "findings": [], "read_count": 0, "unread": [], "limitations": [], "error": "the diff is %s bytes, over the Codex leg limit of 900000 (the runner refuses an input over 1048576 characters, prompt included); rerun the leg by hand in review mode, docs/agents/ai-setup.md step 3"}\\n' "$size"
else
python3 '${top}/.agents/skills/claudex-loop/scripts/runner.py' inspect --host claude --builder claude --repo '${worktree}' --plan '${specFile}' --base '${baseCommit}'${codexModel ? ` --model ${codexModel}` : ''}${codexEffort ? ` --effort ${codexEffort}` : ''} --timeout 570 > '${run}/codex.out' 2> '${run}/codex.err'
python3 - '${run}' <<'PY'
${summarizeCodex}
PY
fi`
  const relayPrompt = `You relay an independent Codex inspection. You do not review code yourself, and you never add, drop or reword a finding.

1. Run exactly this command with the Bash tool, in the foreground, with the tool timeout set to 600000 ms. Add no flags, pipes or redirects:
\`\`\`bash
${runnerCommand}
\`\`\`
2. Its last stdout line is one JSON object. Return it as your structured output with every field copied verbatim.
3. If there is no such line (for example the tool call timed out), return available false, verdict NONE, empty findings, unread and limitations, read_count 0, and error quoting what the shell reported.`

  phase('Review')
  // One leg at a time, Codex first: the runner has a 570 s budget inside a foreground shell, and kit-reviewer's builds
  // and test runs in the same worktree starved it twice (2026-10-07, #52 and #53: 570 s inside the workflow, about
  // 250 s alone). Sequential costs a few minutes of wall-clock and removes the hand rerun.
  const codexRaw = await parallel([
    () => agent(relayPrompt, { label: 'review:codex', phase: 'Review', effort: 'low', schema: CODEX_REVIEW }),
  ]).then((legs) => legs[0])
  const claudeRaw = await parallel([
    () => agent(reviewerPrompt, { label: 'review:kit-reviewer', phase: 'Review', agentType: 'kit-reviewer', schema: CLAUDE_REVIEW }),
  ]).then((legs) => legs[0])
  const missing = (note) => ({ available: false, verdict: null, findings: [], read_count: 0, unread: [], limitations: [], note })
  const claude = claudeRaw ?? missing('kit-reviewer did not return')
  const codex = codexRaw ?? missing('the Codex relay did not return')

  // A leg counts as a review only when it says it ran, read the diff, left none of it unread, and its verdict fits
  // its findings. kit-reviewer's "unknown" (part of the diff unread) and Codex's BLOCKED (required evidence not
  // inspected) do not count.
  const gap = (review, reviewed, hasOpen, cleanVerdict) =>
    review.available !== true ? (review.error || review.note || 'the review did not run to completion')
    : !reviewed.includes(review.verdict) ? `verdict "${review.verdict}" is not a completed review`
    : !(review.read_count > 0) ? 'it reports reading no changed file'
    : list(review.unread).length ? `part of the diff went unread: ${list(review.unread).join('; ')}`
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

Two independent reviewers examined commit ${head} against base ${baseCommit} for ${source}; its text is in ${specFile}. The commit is checked out in ${worktree}: open every cited path under that directory, and nowhere else. Their findings, each with an id:
${JSON.stringify(raised, null, 2)}

1. Merge findings that name the same file and the same root cause, and list every merged id in \`sources\`. Every input id appears in exactly one output finding. Add no findings of your own.
2. For each one, open the cited code and the spec, ADR or glossary entry it relies on, then set \`status\`:
   - confirmed: the code is as described, and the defect is real and introduced by this change;
   - refuted: the code shows the claim is wrong, or the issue predates ${baseCommit};
   - unverified: you could not establish either. Unverified is not clean.
3. \`claim\` states the defect in one line of at most 160 characters. \`verification\` states what you opened and what it showed, with path:line. Keep the highest severity any source gave unless the evidence supports a higher one.`
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

  // Stable ids: a carried finding keeps its earlier id, every other finding gets the next F-number after the highest
  // one the earlier file ever used (lastId), so an id dropped at extraction is never reused.
  // The id is set last, so nothing a reviewer or the verifier returned can replace it.
  const used = new Set(previous.map((p) => p.id))
  let next = previous.reduce((top, p) => Math.max(top, /^F(\d+)$/.test(p.id) ? Number(p.id.slice(1)) : 0), prepared.lastId ?? 0)
  findings = findings.map((f) => {
    let id = carried.get(f)
    if (!id) {
      do { next += 1 } while (used.has(`F${next}`))
      id = `F${next}`
    }
    const { id: _, ...rest } = f
    return { id, ...rest }
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
    read_count: review.read_count ?? 0,
    unread: list(review.unread),
    limitations: list(review.limitations),
    note: problem ?? review.note ?? null,
  })
  const count = (status) => findings.filter((f) => f.status === status).length
  full = {
    workflow: 'kit-review',
    complete,
    verdict,
    base: baseCommit,
    head,
    ticket: ticket ? Number(ticket) : null,
    spec: spec || null,
    specFile,
    previous: previousFile || null,
    reviewers: { claude: leg(claude, claudeGap), codex: { ...leg(codex, codexGap), effort: codexEffort || null } },
    counts: { confirmed: count('confirmed'), unverified: count('unverified'), refuted: count('refuted') },
    findings,
    rescored,
    lastId: next,
    note: 'Read-only: nothing in the reviewed commit was edited. The main session judges these findings and decides fix or done. To re-review after a high finding was fixed, run kit-review again with previous set to this file. A verdict of "unknown" means a reviewer or the verification stage did not return a usable review; it is not a pass.',
  }
} catch (error) {
  log(`Review failed: ${error?.message ?? error}`)
  full = { workflow: 'kit-review', complete: false, verdict: 'unknown', base: baseCommit, head, error: String(error?.message ?? error) }
} finally {
  // Runs on success and on failure, so the review worktree never outlives the run. Writing the full result rides on
  // this agent call, which removing the worktree needs anyway. A quoted heredoc copies the text unchanged into the run
  // directory, and the result counts as saved when the verdict parsed back from its copy under .claude/reviews is the
  // one written; one retry when it is not. Non-ASCII is escaped, so the length is the byte count, kept as information.
  phase('Finish')
  const written = full ?? fail('the review stopped early')
  const content = `${JSON.stringify(written, null, 2)}\n`
    .replace(/[\u007f-\uffff]/g, (c) => `\\u${c.charCodeAt(0).toString(16).padStart(4, '0')}`)
  const finishPrompt = `You save a review result and remove a git worktree. You do not review or change anything else.

1. Run exactly this command with the Bash tool, in the foreground, as one command: every line between the two fence lines, byte for byte. Add no flags, pipes or redirects, and reformat nothing.
\`\`\`bash
cat > '${resultFile}' <<'KIT_REVIEW_RESULT'
${content}KIT_REVIEW_RESULT
mkdir -p '${reviewDir}' && cp '${resultFile}' '${reviewFile}'
python3 -c "import json,sys; d=json.load(open(sys.argv[1])); print(d['verdict'])" '${reviewFile}'; wc -c < '${reviewFile}'; git -C '${top}' worktree remove --force '${worktree}'; test -e '${worktree}' && echo kept || echo removed
\`\`\`
2. Return verdict (the line python printed; an empty string when it printed an error instead), bytes (the number wc printed), removed (true when the last line is "removed") and error (any error line the command printed, verbatim; omit it when there is none).`
  for (let attempt = 1; attempt <= 2 && !saved; attempt++) {
    try {
      const got = await agent(finishPrompt, { label: attempt === 1 ? 'finish' : 'finish-retry', phase: 'Finish', effort: 'low', schema: FINISHED })
      finished = { ...got, removed: !!got.removed || !!finished?.removed }
      saved = String(got.verdict ?? '').trim() === written.verdict
    } catch (error) {
      log(`Finish failed: ${error?.message ?? error}`)
    }
    if (!saved && attempt === 1) log('The result file did not read back with the verdict written; running the finish step once more.')
  }
  full = { ...written, size: content.length }
}

// The digest is what reaches the main session, so it stays well under the task notification's limit (about 9.8 KB)
// for any number of findings: what must not be lost (verdict, result file, cleanup) comes first, then one short line
// per finding, standing ones first and most severe first. Claims shrink, then the list is cut with a count of the
// rest, which the result file holds.
const DIGEST_LIMIT = 5500
const cleanup = finished?.removed ? null : `The review worktree was not removed; run \`git worktree remove --force ${worktree}\`.`
if (cleanup) log(cleanup)
if (!saved) log(`The full result file was not written as expected (${finished ? `it read back verdict ${JSON.stringify(finished.verdict)}, ${finished.bytes} of ${full.size} bytes` : 'the finish agent did not return'}); the full result is in this return value.`)
const oneLine = (value, max) => {
  const flat = String(value ?? '').replace(/\s+/g, ' ').trim()
  return flat.length > max ? `${flat.slice(0, max - 1)}…` : flat
}
const legNote = (r) => (!r ? null : !r.available ? oneLine(r.note, 300)
  : `${r === full.reviewers?.codex ? `${r.read_count} coverage entries` : `read ${r.read_count} changed files`}, ${r.findings} finding${r.findings === 1 ? '' : 's'}`)
const RESCORED_MAX = 60
const header = {
  workflow: 'kit-review',
  complete: full.complete,
  verdict: full.verdict,
  result: saved ? reviewFile : null,
  run,
  ...(cleanup ? { cleanup } : {}),
  ...(full.error ? { error: oneLine(full.error, 300) } : {}),
  note: saved
    ? `Full result (evidence, fix and verification per finding; each leg's limitations) in ${reviewFile}. Re-review after a high fix: previous = that file. "unknown" is never a pass.`
    : 'The result file was not written; the full result is under `full` at the end. "unknown" is never a pass.',
  base: full.base,
  head: full.head,
  ...(ticket ? { ticket: Number(ticket) } : { spec }),
  counts: full.counts ?? null,
  legs: full.reviewers
    ? { claude: { verdict: full.reviewers.claude.verdict, note: legNote(full.reviewers.claude) }, codex: { verdict: full.reviewers.codex.verdict, note: legNote(full.reviewers.codex) } }
    : null,
  ...(list(full.rescored).length ? {
    rescored: Object.fromEntries(full.rescored.slice(0, RESCORED_MAX).map((r) => [r.id, r.state])),
    ...(full.rescored.length > RESCORED_MAX ? { rescoredOmitted: full.rescored.length - RESCORED_MAX } : {}),
  } : {}),
}
const order = (f) => [f.status === 'refuted' ? 1 : 0, -SEVERITY.indexOf(f.severity), f.status === 'confirmed' ? 0 : 1]
const ordered = [...list(full.findings)].sort((a, b) => {
  const [x, y] = [order(a), order(b)]
  return x[0] - y[0] || x[1] - y[1] || x[2] - y[2]
})
const line = (f, claimMax) => ({
  id: f.id,
  severity: f.severity,
  status: f.status,
  path: oneLine(f.path, 120),
  by: list(f.raisedBy).join('+'),
  claim: oneLine(f.claim || f.evidence, claimMax),
})
const size = (value) => JSON.stringify(value).length
const claimMax = [160, 100, 60].find((max) => size({ ...header, findings: ordered.map((f) => line(f, max)) }) <= DIGEST_LIMIT) ?? 60
const lines = ordered.map((f) => line(f, claimMax))
// Keep the longest prefix that fits beside the header and the omitted count; one comma separates each line.
let room = DIGEST_LIMIT - size({ ...header, findings: [], omitted: lines.length })
let kept = 0
while (kept < lines.length && room - size(lines[kept]) - 1 >= 0) room -= size(lines[kept++]) + 1
let out = { ...header, findings: lines.slice(0, kept), ...(kept < lines.length ? { omitted: lines.length - kept } : {}) }
if (kept < lines.length) log(`The digest lists ${kept} of ${lines.length} findings; the rest are in the result file.`)
if (!saved) out = { ...out, full }
return out
