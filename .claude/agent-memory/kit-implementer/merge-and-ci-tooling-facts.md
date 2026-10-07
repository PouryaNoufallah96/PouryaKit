---
name: merge-and-ci-tooling-facts
description: Retro after Wave 2 batch 4 - union merge behaviour on the append-only files, reading a failed CI job log with gh, and proving it all from a worktree
metadata:
  type: project
---

- **`merge=union`** (`.gitattributes`, since 2026-10-07) covers `docs/acceptance.md` and this `MEMORY.md`. Two branches that each append a section merge with exit 0 and both sections present; at `7e3aede` (no attribute) the same merge conflicts in both files. Union drops the blank line both appended sections start with, so the second heading follows the first section's last line (an ATX heading still renders). A line both branches changed is kept twice, silently: read the merged file.
- Prove a merge behaviour in a scratch clone driven by a Python script in the scratchpad (`git clone --branch <your branch> <worktree> <scratch>/x`, then `git -C <scratch>/x ...` through `subprocess`, with `-c core.hooksPath=/dev/null` and a throwaway identity). Run it once at the base commit as the negative control.
- **Failed CI logs:** `gh api repos/<o>/<r>/actions/jobs/<id>/logs` writes zero bytes and prints "the response contains terminal escape sequences" unless given `--allow-escape-sequences` (gh 2.102.0). `gh run view --job <id> --log-failed` works without it, but printed `UNKNOWN STEP` on every line and the whole job log (32,527 lines), with each escape character as the two characters `^[`. Either way the blob download is about 75 KB/s, so a 3.7 MB job log takes 45 to 50 s; `.agents/scripts/ci-failures.py` runs in about 52 s.
- MTP failure shape in the log: `failed <FQN> (<duration>)`, then indented lines (`from <dll>`, the assertion message, `at` frames); the assembly summary `<dll> failed with N error(s)` is not indented. Log lines such as `ReadFolderQuery failed with Reading.Missing` do not start with `failed`.

**Why:** these took probes during the batch-4 retro package; the CI log path decides how long the orchestrator waits.
**How to apply:** when a merge proof, a CI failure or a `gh` log download is part of the work. Related: [[worktree-and-check-gotchas]], [[workflow-script-facts]].
