---
name: merge-and-ci-tooling-facts
description: Retro after Wave 2 batch 4 and session 5 - union merge behaviour on the append-only files, reading a failed CI job log with gh, testing a git-driving script in throwaway repositories
metadata:
  type: project
---

- **`merge=union`** (`.gitattributes`, since 2026-10-07) covers this `MEMORY.md` only: `docs/acceptance.md` left it on 2026-10-08 (session-5 retro), after union put a new section inside one of its tables, so two packages that both append there now conflict and the merge keeps both sections in ticket order. Two branches that each append a section merge with exit 0 and both sections present; at `7e3aede` (no attribute) the same merge conflicts in both files. Union drops the blank line both appended sections start with, so the second heading follows the first section's last line (an ATX heading still renders). A line both branches changed is kept twice, silently: read the merged file.
- Prove a merge behaviour in a scratch clone driven by a Python script in the scratchpad (`git clone --branch <your branch> <worktree> <scratch>/x`, then `git -C <scratch>/x ...` through `subprocess`, with `-c core.hooksPath=/dev/null` and a throwaway identity). Run it once at the base commit as the negative control.
- **Failed CI logs:** `gh api repos/<o>/<r>/actions/jobs/<id>/logs` writes zero bytes and prints "the response contains terminal escape sequences" unless given `--allow-escape-sequences` (gh 2.102.0). `gh run view --job <id> --log-failed` works without it, but printed `UNKNOWN STEP` on every line and the whole job log (32,527 lines), with each escape character as the two characters `^[`. Either way the blob download is about 75 KB/s, so a 3.7 MB job log takes 45 to 50 s; `.agents/scripts/ci-failures.py` runs in about 52 s.
- MTP failure shape in the log: `failed <FQN> (<duration>)`, then indented lines (`from <dll>`, the assertion message, `at` frames); the assembly summary `<dll> failed with N error(s)` is not indented. Log lines such as `ReadFolderQuery failed with Reading.Missing` do not start with `failed`.

- **Testing a git-driving script** (`test_merge_package.py`, 2026-10-08): pass one env to both the test's git calls and the script's process, with every inherited `GIT_*` variable dropped and `GIT_CONFIG_GLOBAL=/dev/null`, `GIT_CONFIG_NOSYSTEM=1`, then set identity and `core.hooksPath=/dev/null` in the throwaway repository's own config. Keep stub check scripts outside the repository, or they make its checkout dirty. A bare `origin.git` beside it proves a push or its absence by `rev-parse main` there. Mutation proof without touching the worktree: copy the script and its test module into one scratchpad folder per mutation (the tests find the script with `Path(__file__).with_name`), mutate the copy, run `unittest discover -s <folder>`.
- From an isolated worktree agent, `git -C <main checkout>` is refused like any other redirect; read the main checkout's state from the worktree (they share refs and config).

**Why:** these took probes during the batch-4 retro package; the CI log path decides how long the orchestrator waits.
**How to apply:** when a merge proof, a CI failure or a `gh` log download is part of the work. Related: [[worktree-and-check-gotchas]], [[workflow-script-facts]].
