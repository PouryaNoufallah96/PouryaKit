---
name: core-helpers-facts
description: Ticket #37 facts - C# 14 extension blocks emit public nested types, adding a src package breaks Sample.KitPackages, offending copy of a Kit package via linked sources and an alias, local validate.py run
metadata:
  type: project
---

Verified in ticket #37 (2026-10-07, PouryaKit.Core):

- **A C# 14 `extension<T>(...)` block emits public nested types** (`Outer+<G>$<hash>`1` and `+<M>$<hash>`), `IsSpecialName` true but no `[CompilerGenerated]`. `GetExportedTypes()` lists them, so any public-type count or list must drop names starting with `<`.
- **A new `src/` package breaks the architecture tests until they reference it:** `Sample.KitPackages` calls `Assembly.Load(folderName)` for every `src/*` folder. Also bump `PackageGraphTests`' package count, add a `PackageGraph.Table` row, and add it to `scripts/packaging/inventory.json` (validate.py, publication workflow and the inventory controls all read that file; nothing else enumerates packages).
- **Negative case for a package-shape rule:** `tests/PouryaKit.ArchitectureTests/Offending.Core` compiles `..\..\..\src\PouryaKit.Core\*.cs` (Compile Include with LinkBase) plus one extra public type; the arch csproj references it with `Aliases="OffendingCore"` and `extern alias` so its same-named types do not clash. Add the folder to the arch csproj's `Compile Remove`/`None Remove` and to the slnx.
- **Expression parameter substitution must not reuse a given parameter** (review finding, #37 fix round): replacing the right lambda's parameter by the left one's rebinds a nested lambda in the right body that declares the left's `ParameterExpression` instance (`p > q` became `p > p`). Put both bodies on a fresh `Expression.Parameter`; a lambda-written test cannot reproduce it, only a hand-built tree sharing the instance.
- `Assert.Throws<T>(() => { ...; throw ...; })` binds the `Func<Task>` overload (CS0619 under xunit.v3); pass a `static void` local function instead.
- A `ref struct` implementing `IDisposable` works with `using` and inside a non-async lambda or local function.
- `python3 scripts/packaging/validate.py --no-fetch --output <scratchpad dir>` runs fine from a worktree after the commit (clean tree required): about 5 minutes, Docker needed, 403 runtime tests at #37, then 14 packages and 16 clean consumers.
- "Thirteen" packages is spelled out in many files outside a typical package's allowed list: `docs/packages.md`, `docs/ci.md`, `docs/private-packages.md`, skill `references/packages.md` and `publication.md`, the publication workflow's step names, and the plan outside section C's table.

**Why:** each cost a retry or a decision in #37.
**How to apply:** when adding a Kit package, a public-type rule, or an offending fixture. Related: [[architecture-test-facts]], [[dotnet-test-conventions]].
