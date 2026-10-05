---
name: worktree-inherits-main-checkout
description: Worktrees live under the main checkout, so dotnet picks up its global.json even when the worktree branch has none; spikes need stoppers
metadata:
  type: project
---

Implementer worktrees sit at `<repo>/.claude/worktrees/<id>/`, inside the main checkout. dotnet's upward search for `global.json` (and MSBuild's for `Directory.Build.props` / `Directory.Packages.props`) therefore reaches the main checkout's root files, even when the worktree's own branch predates them. Seen in ticket #3 (2026-10-05): the scaffold (#4) merged into `feat/wave-1` mid-package, its `global.json` set `"test": {"runner": "Microsoft.Testing.Platform"}`, and a run that passed on VSTest earlier then failed with "global.json defines test runner to be Microsoft.Testing.Platform. All projects must use that test runner."

**Why:** the final `verify` run must pass, and it runs in whatever the main checkout currently holds, which the post-merge state will match.

**How to apply:**
- A self-contained folder (a Spike) gets its own `Directory.Build.props` and `Directory.Packages.props` stoppers (`ManagePackageVersionsCentrally=false`, inline versions). `global.json` cannot be stopped this way, so test projects follow the repo runner: `xunit.v3` on MTP, never `xunit.v3.mtp-off` / VSTest packages. See [[dotnet-test-conventions]].
- Before the final verify, check `git log -1 feat/wave-1` against your base; if it moved, re-read its root build files.
- Citing your own commit hash in a doc: when a later fix must be folded into that commit, `git reset HEAD~1` (mixed), amend, re-point the hash, recommit. Never cite a hash before the code is final.
