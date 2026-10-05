---
name: code-style-analyzer-facts
description: How to list the SDK's real IDE/CA diagnostic IDs, which C# 14 idioms have one in SDK 10.0.401, and why solution builds look instant
metadata:
  type: reference
---

- To know which code-style rules really exist in the pinned SDK, reflect over `DiagnosticAnalyzer.SupportedDiagnostics` in `<sdk>/Sdks/Microsoft.NET.Sdk/codestyle/cs/*.CodeStyle.dll` (and `analyzers/*NetAnalyzers.dll`), resolving Roslyn from `<sdk>/Roslyn/bincore`, via a `dotnet run file.cs` script in the scratchpad. SDK 10.0.401: IDE0028, IDE0300-0306, IDE0290, IDE0032 (also flags `field`-keyword cases) and IDE0330 exist; nothing for extension members, TimeProvider or Guid.CreateVersion7.
- A full `dotnet build PouryaKit.slnx --no-incremental` takes about 2 s on this machine with a warm compiler server and still compiles all 27 projects (count `bincore/csc` lines at `-v:n`). Not a sign that compilation was skipped.
- `new[] { ... }` assigned to `var` or used in `foreach` has no target type, so IDE0300 correctly ignores it.
- `brew install actionlint` works on this machine (2026-10-06).

**Why:** retro package C (2026-10-06) spent its first steps proving these.
**How to apply:** before adding or raising an analyzer rule in `.editorconfig`.
