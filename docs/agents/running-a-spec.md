# Running a spec

How `/implement-spec` runs in this repository, and what a session that drives more than one ticket must know. The steps and gates of each ticket are in `AGENTS.md`, "Workflow for every ticket".

## What changes from the skill

The vendored skill holds in shape: an integration branch, a frontier computed from the tracker's blocking edges, one worktree per ticket, implementers in parallel where their files are disjoint. Five of its steps are replaced:

| The skill says | Here |
|---|---|
| An implementer subagent calls `tdd` | A `kit-implementer` with a brief: `objective`, `allowed_files`, `verify`, `stop_if`, and why the work matters. |
| One `code-review` over the integration branch at the end | Review per ticket, sized as workflow step 3 says. The wave's last ticket is the close-out, where `/archify-review` runs once (step 6). |
| A draft pull request | None. A ticket closes with `gh issue close <n> --comment` as soon as it is merged into the integration branch and its gates pass, so the blocking edges give the next frontier. |
| A merger subagent | The orchestrator merges a clean branch itself (`git merge --no-ff`); a merger subagent only on conflicts. |
| The implementer merges the integration tip before reporting | Not required. The orchestrator's merge, and its test run on the merged tree, cover it. |

## Worktrees

- Implementer worktrees start from the orchestrator's commit (`docs/agents/ai-setup.md`, "Subagent worktrees"); the role file's pre-flight is the backstop.
- The orchestrator stays in its checkout and reaches a worktree with `git -C <path>`; a `cd` would move the session's working directory.
- A worktree nested in the checkout inherits `global.json` and `Directory.*.props` from above when its own branch lacks them. A self-contained folder needs stoppers: see `docs/agents/spikes.md`.
- A review reads a detached worktree, so the checkout stays free for merges. `/kit-review` creates and removes its own (step 3). For a single `kit-reviewer`, create one with `git worktree add --detach .claude/worktrees/review-<name> <commit>`, name it in the brief, and `git worktree remove` it afterwards. The reviewer may build and run tests there.

## State and handoff

- **One session owns the integration branch's checkout.** A second session works only in worktrees and signals through the tracker or a pushed tag. Hand off at a ticket boundary with nothing running: a session that ran eleven tickets without a handoff died with "Prompt is too long" before closing the last one.
- **The tracker is the state.** The orchestrator's run-state memory note holds only what the tracker cannot: the integration branch, what is running now, and owner decisions not yet posted. Keep it under about 40 lines; narrative goes in closing comments, stable knowledge in this document.
- **The closing comment is the ticket's record:** what landed and at which commits, the review and the disposition of each finding, a value, cost and impact paragraph (the `archify-review` lens), and the decisions and seams later tickets depend on. Copy commit hashes from command output.
- **A brief carries the ticket's own content and scope statement**, as `AGENTS.md`, "Operating model", requires. For CI and packaging, apply its standard-tool and early external-probe rules before extending the implementation. The worktree base, the status (`README.md`) and the C# idioms (the build and `.claude/rules/kit-code.md`) are covered elsewhere.

## Spikes

A Phase that opens with Spikes follows `docs/agents/spikes.md`. Name that document in each Spike's brief and in the brief of the review of its record: no rule loads for `spikes/` or `docs/adr/`, so the brief is how it reaches them.
