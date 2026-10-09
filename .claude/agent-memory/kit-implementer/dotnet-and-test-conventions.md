---
name: dotnet-and-test-conventions
description: Test platform (MTP, xunit.v3 4.x), analyzer and compiler traps under TreatWarningsAsErrors, mutation-run recipes and their compile traps, and the sample tests every model change touches
metadata:
  type: project
---

## Test platform and test techniques (#4, 2026-10-05; SDK 10.0.401, xunit.v3 4.0.1)

- Test projects use xunit.v3 4.0.1, which pulls xunit.v3.mtp-v2 (Microsoft.Testing.Platform v2). `global.json` sets `"test": {"runner": "Microsoft.Testing.Platform"}`, so `dotnet test PouryaKit.slnx` (positional path) runs in MTP mode on SDK 10.0.401.
- A test csproj is `OutputType Exe`, `IsPackable false`, `PackageReference xunit.v3` (version in Directory.Packages.props), `<Using Include="Xunit" />`. Never add Microsoft.NET.Test.Sdk or xunit.runner.visualstudio.
- MTP fails a test project that runs zero tests (exit code 8), so a new test project needs at least one real test before it joins the solution.
- VSTest flags (`--filter`, `--logger`) make MTP print help and run nothing (exit 5). Filter with `dotnet test --project <csproj> --filter-class <FQN> --filter-method "*Name*" --output Detailed`; `--output Detailed` shows passed tests' output. Say which runner produced a result you report.
- On xunit.v3 4.x, `[assembly: CollectionBehavior(DisableTestParallelization = true)]` is CS0619; use `[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]` to run tests one at a time.
- `nuget.config` clears sources to nuget.org only: with central package management plus TreatWarningsAsErrors, a second source without mapping turns NU1507 into a restore error.
- xUnit analyzer xUnit1051 is an error under TreatWarningsAsErrors: any test call to a method taking a CancellationToken must pass `TestContext.Current.CancellationToken`.
- Ticket #7 (2026-10-05): Cqrs and Validation test projects reference `Microsoft.Extensions.DependencyInjection` (10.0.12, test-only, recorded as a deviation) to build a strict container; logging in tests is an own `ILogger<>` open-generic stand-in or `NullLogger<>`, never `Microsoft.Extensions.Logging`.
- CI's setup-dotnet reads global.json; `rollForward: latestPatch` maps to `10.0.4xx`, `latestFeature` would float to any 10.0.
- `Assert.Throws<T>(() => { ...; throw ...; })` binds the `Func<Task>` overload (CS0619 under xunit.v3); pass a `static void` local function instead.
- **xUnit truncates a long string list in a failure.** To see a rule's full output once, temporarily `Assert.Fail(string.Join("\n", list))`, then restore from a scratchpad copy.
- An `AsyncLocal` write inside an awaited async method never flows back to the caller, so asserting `DomainClock.Current` after awaiting the dispatcher cannot fail; test binding restore synchronously (`tests/ModularMonolith.Tests/TestClockTests.cs`).

## Analyzers and the compiler (retro C, 2026-10-06; SDK 10.0.401)

- To know which code-style rules really exist in the pinned SDK, reflect over `DiagnosticAnalyzer.SupportedDiagnostics` in `<sdk>/Sdks/Microsoft.NET.Sdk/codestyle/cs/*.CodeStyle.dll` (and `analyzers/*NetAnalyzers.dll`), resolving Roslyn from `<sdk>/Roslyn/bincore`, via a `dotnet run file.cs` script in the scratchpad. SDK 10.0.401: IDE0028, IDE0300-0306, IDE0290, IDE0032 (also flags `field`-keyword cases) and IDE0330 exist; nothing for extension members, TimeProvider or Guid.CreateVersion7.
- A full `dotnet build PouryaKit.slnx --no-incremental` takes about 2 s on this machine with a warm compiler server and still compiles all 27 projects (count `bincore/csc` lines at `-v:n`). Not a sign that compilation was skipped.
- `new[] { ... }` assigned to `var` or used in `foreach` has no target type, so IDE0300 correctly ignores it.
- `brew install actionlint` works on this machine (2026-10-06).
- C# 14 `field` keyword on a get-only property assigned in only one of two constructors (`Result<T>.Value`) fails with CS9264 under nullable; `[field: MaybeNull]` plus `field!` in the getter fixes it. Assigning a get-only `field`-backed property in a constructor writes the backing field.
- A `ref struct` implementing `IDisposable` works with `using` and inside a non-async lambda or local function.
- `ImmutableDictionary<,>.Empty` in a field initialiser trips IDE0301 under TreatWarningsAsErrors; write `[]`.
- **xUnit1069 (#75, 2026-10-09):** a `[Fact(Timeout = …)]` whose body never names `TestContext.Current.CancellationToken` fails the build. Helpers that use the token inside do not count; a test whose waits all have deadlines can take a plain `[Fact]`, as most broker tests do.
- **A red run on an older package version (#75):** set the pins back in `Directory.Packages.props` with `sed`, run the class with `--filter-method <full name>` (it restores and rebuilds), then `cmp` the test bin's dll with `~/.nuget/packages/<id>/<old>/lib/net10.0/` to prove which version ran; set the pins forward and run again.
- **IDE0130 in a build (#77, 2026-10-09):** works with no `CompilerVisibleProperty` (the SDK adds `RootNamespace` and `ProjectDir`); an `.editorconfig` section `[tests/**/*.cs]` with `dotnet_diagnostic.IDE0130.severity = warning` fails the next incremental build on a mismatch, and a later `none` section for one file wins. A linked source is judged by the sections of its physical path, so `Offending.Core`'s Core files fall under `src/**`. To test an exception, rename its section header to a path that matches nothing, build the one project, then copy the file back from the scratchpad.

## Moving types in the test tree (#79, 2026-10-09; SDK 10.0.401)

- **IDE0005 reports a run of adjacent unnecessary usings as one diagnostic at its first line:** `IntegrationEventWiringTests.cs(1,1)` covered both `using System.Reflection;` and `using Billing;`. Remove every adjacent unused using, then build again until none is reported. Each type cut out of a file, and each file moved into a child namespace, strands usings the build then fails on: `using ModularMonolith.Tests;` is redundant inside `ModularMonolith.Tests.Fixtures`, whose enclosing namespaces are in scope.
- `ModularMonolith.Tests`' infrastructure is in `Fixtures/` (namespace `ModularMonolith.Tests.Fixtures`, one `<Using>` in the csproj): a new fixture, probe or helper goes there, a test class at the root. Root code still sees root types first, so a fixture type now arrives through a using like the Stub's and could become ambiguous (CS0104) with a same-named imported type; none did.
- `git mv` of a project folder moves its untracked `bin/` and `obj/` with it; move them to the scratchpad first (`mv`, as `rm -r` is refused), so no stale `obj/` sits inside the new parent project. A project nested in a test project needs `Compile Remove` and `None Remove` for its folder before the first build, or the parent compiles its sources and its `obj/*.cs`.

## Mutation runs and their compile traps

- Mutation via `if (false && …)` fails the build (CS0162 unreachable code under TreatWarningsAsErrors); use a runtime-false condition.
- `if (true)` in a mutation fails the build (CS0162 under TreatWarningsAsErrors); use a non-constant condition.
- Mutation code under TreatWarningsAsErrors: `if (false)` fails the build (CS0162); use `DateTime.Now.Year < 0`.
- Mutation trap: after `if (x is null) return null;`, a later `if (x is null && …)` makes `x` maybe-null again and the build fails CS8604; mutate without naming `x`. An own-connection mutation can use `((ICloneable)database.GetDbConnection()).Clone()`.
- **Mutation compile traps:** dropping the only use of a primary-constructor parameter fails CS9113 (keep a read: `(timeProvider is null ? timeProvider! : TimeProvider.System)`); a catch filter like `exception is null` makes `exception` maybe-null (CS8604). `mutate42f.py` in the session scratchpad ran 7 mutations, all red.
- **Mutation trap:** `!await X | flag` parses as `(!await X) | flag`; write `!(flag | await X)` to make a gate accept everything. Three Guid v7 made in one millisecond do not sort in creation order (assert membership, not order).
- Mutation scripts `mutate61.py` and `mutate61fix.py` (several replacements per mutation) in the session scratchpad: 13 mutations, each restored from a backup in `finally`. `x ?? (DateTime.Now.Year > 0 ? new T() : null) ?? throw ...` disables a `?? throw` guard without a constant-condition warning.
- Mutating away a call to a private helper without an unused-member build error: keep the call, `(Helper(x) is var _ ? alternative : null)`.
- Mutation runs: a python script in the scratchpad, run with the worktree as argument, copies the file, replaces one string, runs `dotnet test --filter-class` with `cwd=` the worktree, prints the red tests, and copies back in `finally`. A combined `python3 - <<EOF` plus `dotnet test` in one Bash call was refused; write the script with Write first.
- Runner: `scratchpad/mutate42.py` (one `dotnet test --filter-method` per mutation, file restored in `finally`, files backed up first). 15 mutations took about 8 minutes.
- Mutation script `scratchpad/mutate17.py` (apply, run filtered classes or `m:` methods, restore). A mutated constant used in `Task.Delay` made one run take 5 min: filter to the target method.
- **Mutation script regex:** a Theory case name contains spaces; parse `^failed (.*) \(\d[^)]*\)$`, not `\S+`.
- A mutation script that runs `dotnet test --filter-class` must use the worktree as cwd: from the scratchpad there is no `global.json` with the MTP runner, so VSTest mode answers `MSB1001: Unknown switch` (exit 1, no tests).
- **Harness: the VSTest fallback.** A `dotnet test --project` launched with a working directory that has no `global.json` above it runs VSTest and fails with MSB1001. Run mutation scripts with `cwd` inside the repository.

## Sample and Kit tests a change ripples into

- After #10 a Product needs an existing Category (`CountProductInCategoryHandler` fails NotFound otherwise): any Catalog test creating products sends `CreateCategoryCommand` first. Parallel tickets that touch the sample model break each other's branches; expect a post-merge repair round.
- `tests/PouryaKit.Cqrs.Tests` holds deliberately invalid operations: never `AddPouryaKitCqrs(thatAssembly)`; register test handlers by hand.
- `PersistenceTests.Each_module_has_its_own_schema...` asserts the exact table list and migration names: any new table or migration edits it (merge-conflict hotspot with parallel tickets).
- Adding a Module to the sample breaks `PersistenceTests` (schema and DbContext lists) and `NotificationTests.A_new_product_is_announced…` (exact delivery list).
- Adding a subscription to Product changes exact delivery lists (`NotificationTests`) and unfiltered `inventory.pouryakit_receipts` counts (`IntegrationEventTests`); filter by subscription.
- Publishing a second Integration Event type changes the exact delivery list in `NotificationTests.A_new_product_is_announced...`; `IndependentDeliveryTests` already filters by wire name. Any new sample table changes the exact table list in `PersistenceTests.Each_module_has_its_own_schema_and_migrations_history_with_snake_case_names`.

**Why:** Test projects run on Microsoft.Testing.Platform, chosen in ticket #4 (2026-10-05) as the plain current .NET 10 idiom; a later ticket repeating VSTest packages would create a second way of running tests. Each other fact cost a build, a run or a vacuous red in tickets #4 to #63 or retro C (2026-10-06).
**How to apply:** before adding a test project, a test filter or an analyzer rule in `.editorconfig`; before every mutation run; when a change adds a table, a Module, a subscription or an Integration Event type to the sample. Related: [[worktree-shell-and-check-gotchas]], [[core-json-and-architecture-test-facts]].
