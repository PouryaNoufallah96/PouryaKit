# Running a spec

How `/implement-spec` runs in this repository, and what a session that drives more than one ticket must know. The steps and gates of each ticket are in `AGENTS.md`, "Workflow for every ticket".

## What changes from the skill

The vendored skill holds in shape: an integration branch, a frontier computed from the tracker's blocking edges, one worktree per ticket, implementers in parallel where their files are disjoint. Five of its steps are replaced:

| The skill says | Here |
|---|---|
| An implementer subagent calls `tdd` | A `kit-implementer` with a brief: `objective`, `allowed_files`, `verify`, optional `stop_if`, and why the work matters. |
| One `code-review` over the integration branch at the end | Review per ticket, sized as workflow step 3 says. The wave's last ticket is the close-out, where `/archify-review` runs once (step 6). |
| A draft pull request | None. A ticket closes with `gh issue close <n> --comment` as soon as it is merged into the integration branch and its gates pass, so the blocking edges give the next frontier. |
| A merger subagent | The orchestrator merges a clean branch itself (`git merge --no-ff`); a merger subagent only on conflicts. |
| The implementer merges the integration tip before reporting | Not required. The orchestrator's merge, and its test run on the merged tree, cover it. |

## Worktrees

- Implementer worktrees start from the orchestrator's commit (`docs/agents/ai-setup.md`, "Subagent worktrees"); the role file's pre-flight is the backstop.
- The orchestrator stays in its checkout and reaches a worktree with `git -C <path>`; a `cd` would move the session's working directory.
- A worktree nested in the checkout inherits `global.json` and `Directory.*.props` from above when its own branch lacks them. A self-contained folder needs stoppers: see `docs/agents/spikes.md`.
- The review `base` is the `base` the implementer's report names, not the orchestrator's tip: implementers fast-forward or rebase onto a moving integration branch, and a Spike's branch starts from an older tip. A Spike record lands by `git cherry-pick` of its record commit after the `spike/<name>` tag is pushed; the code commit is never merged.
- A review reads a detached worktree, so the checkout stays free for merges. `/kit-review` (`.claude/workflows/kit-review.js`) creates and removes its own (step 3). For a single `kit-reviewer`, create one with `git worktree add --detach .claude/worktrees/review-<name> <commit>`, name it in the brief, and `git worktree remove` it afterwards. The reviewer may build and run tests there.

## State and handoff

- **One session owns the integration branch's checkout.** A second session works only in worktrees and signals through the tracker or a pushed tag. Hand off at a ticket boundary with nothing running, after about five closed tickets, earlier when a ticket needs a second review round: a session that ran eleven tickets without a handoff died with "Prompt is too long" before closing the last one.
- **The tracker is the state.** The orchestrator's run-state memory note holds only what the tracker cannot: the integration branch, what is running now, and owner decisions not yet posted. Keep it under about 40 lines; narrative goes in closing comments, stable knowledge in this document.
- **Reports go to the scratchpad.** As soon as a role's final report arrives, the orchestrator saves it verbatim as `<scratchpad>/report-<ticket>.md`, beside the ticket's evidence file, because subagents cannot write report files here. The run-state memory note names the scratchpad, and the next session reads those reports first.
- **The closing comment is the ticket's record:** what landed and at which commits, the review and the disposition of each finding, a value, cost and impact paragraph (the `archify-review` lens), and the decisions and seams later tickets depend on. Copy commit hashes from command output.
- **A failed CI run:** `python3 .agents/scripts/ci-failures.py <run-id>` prints each failed test with the lines around its assertion, from the failed jobs' logs only. It takes about a minute, most of it the log download.
- **A fix after the normal rounds.** `AGENTS.md` caps a ticket at two full reviews. When the second raises a new high, a fresh `kit-implementer` takes it from a short handoff brief (the ticket, the branch and commit, the findings with their evidence, the files, the ticket's `verify`), because an implementer is resumed for one fix round at most. A single `kit-reviewer` then re-scores those findings only, from a detached worktree of the fix commit (`git worktree add --detach .claude/worktrees/review-<ticket> <commit>`, removed afterwards), and the closing comment records each finding's state. No third full review.
- **A Capability ticket's brief allows the docs-sync set by default**, as `docs/agents/brief-template.md` lists it, so step 6 and 7 edits do not cost a fix round. Package validation is the last, post-commit step: the brief asks for the commit, then `python3 scripts/packaging/validate.py --no-fetch --output <fresh directory outside the checkout>` runs on the committed tree (it refuses uncommitted changes and needs full history and tags) and its PASS line goes in `docs/acceptance.md`.
- **A brief starts from `docs/agents/brief-template.md`** and carries the ticket's own content and scope statement, as `AGENTS.md`, "Operating model", requires. For CI and packaging, apply its standard-tool and early external-probe rules before extending the implementation. The worktree base, the status (`README.md`) and the C# idioms (the build and `.claude/rules/kit-code.md`) are covered elsewhere. The harness supplies each agent's commit attribution, so a brief carries no trailer text.

## Spikes

A Phase that opens with Spikes follows `docs/agents/spikes.md`. Name that document in each Spike's brief and in the brief of the review of its record: no rule loads for `spikes/` or `docs/adr/`, so the brief is how it reaches them.
