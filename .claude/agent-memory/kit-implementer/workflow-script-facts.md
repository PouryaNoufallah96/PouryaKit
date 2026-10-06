---
name: workflow-script-facts
description: Facts about Claude Code workflow scripts (.claude/workflows/*.js) and the claudex-loop runner learned while reworking kit-review
metadata:
  type: project
---

- Direct `node --check` of `kit-review.js` passed on Node v22.22.3 and failed with "Illegal return statement" on v24.21.0. The workflow runtime wraps the body in an async function. If the direct check rejects the workflow body's top-level `return`, check a copy with the body wrapped as `export async function body(args, agent, parallel, phase, log) { ... }`. The same wrapper, built with the AsyncFunction constructor, runs the script with stubbed agents. A syntax check alone does not prove execution in the workflow runtime.
- A script has no fs or process access. Running git, `gh` or writing a file takes an agent call. The runtime keeps only the value the script returns: `<session>/workflows/<runId>.json` (`result`) and, when the notification truncates it at about 9.8 KB, `tasks/<taskId>.output`. The script cannot name either path. `journal.jsonl` keeps each agent's return, not the final value.
- The claudex-loop runner in `inspect` mode accepts a detached worktree under `.claude/worktrees/` as `--repo` (Codex 0.160, gpt-6-astra, 2026-10-06; it completed in about 200 s). It uses `--skip-git-repo-check`, so project trust does not matter.
- When Codex fails, the runner's `result.json` `error` only says "inspect stdout.txt". The real reason is in `<artifacts>/stdout.txt`, as `{"type":"error","message":...}` and `{"type":"turn.failed","error":{"message":...}}` events.

- Writing `￿` in a file through the Write tool once left a literal U+FFFF character in the source, while `\u007f` beside it stayed escaped. After writing a regex with `\u` escapes, check for non-ASCII bytes (`grep -c -P '[^\x00-\x7F]'`).

**Why:** these cost a full investigation to find during the kit-review rework (retro item 1).
**How to apply:** when a brief says to verify a workflow with `node --check`, run that direct check first and report its runtime and result. Use the wrapped check as a fallback if the runtime rejects the workflow body's top-level `return`. See [[worktree-and-check-gotchas]].
