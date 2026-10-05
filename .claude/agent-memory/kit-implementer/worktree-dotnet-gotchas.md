---
name: worktree-dotnet-gotchas
description: Agent worktrees sit under the main checkout, so its global.json (MTP test runner) and nuget.config apply; plus worktree-guard command refusals
metadata:
  type: project
---

Agent worktrees live at `<main checkout>/.claude/worktrees/<id>/`, physically under the main checkout. The .NET SDK and NuGet walk up the directory tree, so the main checkout's `global.json` (`"test": {"runner": "Microsoft.Testing.Platform"}`) and `nuget.config` apply even when they are not in the worktree's branch (observed 2026-10-05, Spike S1).

**Why:** `dotnet test` then runs Microsoft.Testing.Platform. VSTest flags (`--filter`, `--logger`) make it print help and run zero tests (exit 5).

**How to apply:** filter with `dotnet test --project <csproj> --filter-class <FQN> --filter-method "*Name*" --output Detailed`. A self-contained Spike needs its own `Directory.Build.props` / `Directory.Packages.props` stoppers, because the main checkout's root props (TreatWarningsAsErrors, central package management) would otherwise apply. When you report a test result, say which runner produced it.

The worktree guard refuses `cd` to a directory computed at runtime, `find … | xargs sed`, and `docker --format '{{…}}'` inside compound commands. Run plain commands with absolute paths from the worktree root. Read-only reference repos are read with absolute paths, never with `cd` + git. Related: [[wolverine-6-spike-facts]].
