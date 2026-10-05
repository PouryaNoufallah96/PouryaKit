---
name: spike-test-project-gotchas
description: Traps when building a Spike test project by copying S1 (VSTest packages, xunit.v3 4.x parallelization attribute, worktree guard on computed sed/for)
metadata:
  type: feedback
---

- S1's `S1.Tests.csproj` (spikes/s1-atomic-commit) references `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio`. Do not copy them: the Kit convention is `xunit.v3` only on Microsoft.Testing.Platform (root `global.json`, `.claude/rules/kit-code.md`). Keep `OutputType=Exe`.
  **Why:** briefs and kit-code.md forbid VSTest packages; S1 predates that rule.
  **How to apply:** start test csproj from `tests/PouryaKit.Domain.Tests` on feat/wave-1 (xunit.v3 4.0.1), not from S1.
- On xunit.v3 4.0.1, `[assembly: CollectionBehavior(DisableTestParallelization = true)]` is a compile error (CS0619, obsolete). Use `[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]` to run tests one at a time.
- Worktree-isolated agents: the guard refuses Bash with `for` loops or `sed` taking a computed argument (`$m`). Write the paths out literally or use a python heredoc.
- Worktree has no `global.json`; `dotnet test` picks up the main checkout's root `global.json` above it (MTP runner). Filters go after `--`: `dotnet test <dir> --output Detailed -- --filter-class "Ns.Class"`; `--output Detailed` is what shows passed tests' output.

Related: [[wolverine-spike-gotchas]]
