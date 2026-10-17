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
- **IDE0130 skips partial types (#110, 2026-10-11):** Roslyn's analyzer reports only what its fix can move (`IsFixSupported` in `Microsoft.CodeAnalysis.CodeStyle.dll`), so a namespace holding a partial type with a second declaration is never reported, such as a class with `[LoggerMessage]` methods (`LoggedNewProductAnnouncer`): its drift built green on any scope. For a red-once that still compiles, move a non-partial type to its parent namespace (`namespace ModularMonolith;` in `ModularMonolith.Host/CallerPermissions.cs`): callers in the child namespace still see it, so IDE0130 is the only error. Build the one csproj; an `.editorconfig` edit recompiles it.
- **EF Core migrations are the same blind spot (#112, 2026-10-11):** a generated migration is a partial class split with its `.Designer.cs` (the snapshot is partial too), so IDE0130 never reports their namespace; a hand-written single-class migration is checked. `SampleNamespaceTests` reads each sample file's declared namespace with a regex and skips `bin/`, `obj/` and `Migrations/`, as its brief said; the sample's migrations follow their folders today. Red-once: move `LoggedNewProductAnnouncer.cs` to `namespace ModularMonolith;`, which compiles and builds green.

## Naming rules in the build (#88, 2026-10-10; SDK 10.0.401)

- **A naming rule's own `severity` never reaches the build** (the naming-rules doc page says so): the build reads `dotnet_diagnostic.IDE1006.severity = warning`, one line for every naming rule. `required_modifiers` does accept `readonly`, so a group with `static, readonly` tells a `static readonly` field from a mutable static; rules order by specificity, so a `const` group and a `static, readonly` group both win over a bare `private` field group. `private` covers a field with no modifier.
- **A red build hides most of the list:** under TreatWarningsAsErrors the first failing project (Core) stops every project that references it, so the plain build printed 2 of 11 fields. `dotnet build PouryaKit.slnx --no-incremental -p:TreatWarningsAsErrors=false | grep IDE1006` lists them all; `Offending.Core` compiles Core's sources, so each Core warning prints twice (dedupe by `path:line`).
- A throwaway probe field must be read and written, or the compiler's unused-field warnings (CS0169, CS0414) fail the build beside IDE1006; the probe of #88 is in its acceptance section.

## Moving types in the test tree (#79, 2026-10-09; SDK 10.0.401)

- **IDE0005 reports a run of adjacent unnecessary usings as one diagnostic at its first line:** `IntegrationEventWiringTests.cs(1,1)` covered both `using System.Reflection;` and `using Billing;`. Remove every adjacent unused using, then build again until none is reported. Each type cut out of a file, and each file moved into a child namespace, strands usings the build then fails on: `using ModularMonolith.Tests;` is redundant inside `ModularMonolith.Tests.Fixtures`, whose enclosing namespaces are in scope.
- `ModularMonolith.Tests`' infrastructure is in `Fixtures/` (namespace `ModularMonolith.Tests.Fixtures`, one `<Using>` in the csproj): a new fixture, probe or helper goes there, a test class at the root. Root code still sees root types first, so a fixture type now arrives through a using like the Stub's and could become ambiguous (CS0104) with a same-named imported type; none did.
- `git mv` of a project folder moves its untracked `bin/` and `obj/` with it; move them to the scratchpad first (`mv`, as `rm -r` is refused), so no stale `obj/` sits inside the new parent project. A project nested in a test project needs `Compile Remove` and `None Remove` for its folder before the first build, or the parent compiles its sources and its `obj/*.cs`.

## The no-using-change proof (#81, 2026-10-10)

- `git diff <base> -- '*.cs' | grep -E '^[+-]using '` also prints `using var` declarations, every using of a new file, and both sides of a `git mv` whose content changed past rename detection (`EfUnitOfWorkExtensions.cs` to `EfUnitOfWorkDbContext.cs`). New tests that need new usings go in a new file. A Kit-internal using a ticket's own design needs (`Microsoft.Extensions.Options` for the options pattern, `System.Collections.Concurrent` for a cache) is listed, not hidden with fully qualified names.
- An `[InlineData]` argument must be a constant, so a test whose theory data names an `Error` keeps its literal code; pin published codes once with literals and use the symbols everywhere else.
- FluentValidation's `context.AddFailure(propertyName, message)` in a `Custom` rule gives a failure with no error code: the path to the Kit's `Validation.Failed` fallback.

## Mutation runs and their compile traps

- Mutation via `if (false && …)` fails the build (CS0162 unreachable code under TreatWarningsAsErrors); use a runtime-false condition.
- `if (true)` in a mutation fails the build (CS0162 under TreatWarningsAsErrors); use a non-constant condition.
- Mutation code under TreatWarningsAsErrors: `if (false)` fails the build (CS0162); use `DateTime.Now.Year < 0`.
- Mutation trap: after `if (x is null) return null;`, a later `if (x is null && …)` makes `x` maybe-null again and the build fails CS8604; mutate without naming `x`. An own-connection mutation can use `((ICloneable)database.GetDbConnection()).Clone()`.
- **Mutation compile traps:** dropping the only use of a primary-constructor parameter fails CS9113 (keep a read: `(timeProvider is null ? timeProvider! : TimeProvider.System)`); a catch filter like `exception is null` makes `exception` maybe-null (CS8604). `mutate42f.py` in the session scratchpad ran 7 mutations, all red.
- **Mutation trap:** `!await X | flag` parses as `(!await X) | flag`; write `!(flag | await X)` to make a gate accept everything. Three Guid v7 made in one millisecond do not sort in creation order (assert membership, not order).
- Mutation scripts `mutate61.py` and `mutate61fix.py` (several replacements per mutation) in the session scratchpad: 13 mutations, each restored from a backup in `finally`. `x ?? (DateTime.Now.Year > 0 ? new T() : null) ?? throw ...` disables a `?? throw` guard without a constant-condition warning.
- Emptying a one-entry dictionary initializer to `new() { }` fails the build (collection-expression style under TreatWarningsAsErrors): to switch an exception off, rename its key to a path that matches nothing (#112).
- Mutating away a call to a private helper without an unused-member build error: keep the call, `(Helper(x) is var _ ? alternative : null)`.
- Mutation runs: a python script in the scratchpad, run with the worktree as argument, copies the file, replaces one string, runs `dotnet test --filter-class` with `cwd=` the worktree, prints the red tests, and copies back in `finally`. A combined `python3 - <<EOF` plus `dotnet test` in one Bash call was refused; write the script with Write first.
- Runner: `scratchpad/mutate42.py` (one `dotnet test --filter-method` per mutation, file restored in `finally`, files backed up first). 15 mutations took about 8 minutes.
- Mutation script `scratchpad/mutate17.py` (apply, run filtered classes or `m:` methods, restore). A mutated constant used in `Task.Delay` made one run take 5 min: filter to the target method.
- **Restore a mutated file with `shutil.copy` plus `touch`, never `shutil.copy2` (#125, 2026-10-17):** `copy2` puts back the original mtime, older than the mutated build's output, so MSBuild's incremental build keeps the mutated dll and the next full suite fails as if the mutation were still on (three `fa` rows of `LocalizationTests` red in a "green" tree). `touch` the file and rebuild if it happened.
- **`.resx` beside a type, no designer (#125):** the SDK names the compiled resources after the first class in the same-named `.cs` file (DependentUpon convention), so `IStringLocalizer<StubMessages>` finds `Stub/StubMessages.resx` and `StubMessages.fa.resx` (satellite `bin/.../fa/ModularMonolith.Tests.resources.dll`) with no `ResourcesPath`. `class StubMessages;` with a file-scoped namespace works on SDK 10.0.402.
- **Mutation script regex:** a Theory case name contains spaces; parse `^failed (.*) \(\d[^)]*\)$`, not `\S+`.
- A mutation script that runs `dotnet test --filter-class` must use the worktree as cwd: from the scratchpad there is no `global.json` with the MTP runner, so VSTest mode answers `MSB1001: Unknown switch` (exit 1, no tests).
- **Harness: the VSTest fallback.** A `dotnet test --project` launched with a working directory that has no `global.json` above it runs VSTest and fails with MSB1001. Run mutation scripts with `cwd` inside the repository.

## Sample and Kit tests a change ripples into

- After #10 a Product needs an existing Category (`CountProductInCategoryDomainEventHandler`, named `CountProductInCategoryHandler` before #80, fails NotFound otherwise): any Catalog test creating products sends `CreateCategoryCommand` first. Parallel tickets that touch the sample model break each other's branches; expect a post-merge repair round.
- **`53300 too many clients` (#95, 2026-10-10):** from `c3a3467` on this machine, the full `ModularMonolith.Tests` run fails `RecurringCommandTests.An_occurrence_stored_before_its_command_changed_shape_…` on PostgreSQL `53300` every time (four runs, one with no other suite running, and the base itself), while the class alone passes. Each run has its own container: `HostFixture` keeps PostgreSQL's default 100 connections (`BrokerFixture` sets 1000), and that test composes three Hosts on a database of its own. So it is not load from parallel tickets; run the base before calling it transient, and report it rather than rerun past once. **Fixed in #94 (2026-10-10):** the test now composes, stops and disposes each Host in its own `await using (...)` block; a sampler that waits for the run's new `postgres:18.6` container and polls `pg_stat_activity` every 0.25 s (`docker exec <id> psql -U postgres -At -c ...`, in a scratchpad Python script, `--filter-method` on the one test) measured the test database's peak at 75 before and 34 after, the server's at 79 and 38. A single green run proves little here: #97 had two green per-project runs before the fix.
  - #93 (2026-10-10, b644fe9 plus #93): the full run passed 609 of 609 in the first round, then failed this test once on `53300` in the fix round and passed 612 of 612 on the one rerun; #97's two runs passed too. Intermittent here, not every time.
- **A probe that wraps a disposable singleton must forward `Dispose` and `DisposeAsync` (#93 review F3):** DI disposes only the instance it resolved, so `ReadProbe`'s wrapper of `ReadConnectionFactory` leaked every composed Host's Npgsql pool until it did. Proof: compose a Host, resolve the service, open and return a lease, dispose the Host, then `OpenAsync` throws `ObjectDisposedException` (`DapperTests.Disposing_a_host_disposes_its_read_connection_pool`).
- `tests/PouryaKit.Cqrs.Tests` holds deliberately invalid operations: never `AddPouryaKitCqrs(thatAssembly)`; register test handlers by hand.
- `PersistenceTests.Each_module_has_its_own_schema...` asserts the exact table list and migration names: any new table or migration edits it (merge-conflict hotspot with parallel tickets).
- Adding a Module to the sample breaks `PersistenceTests` (schema and DbContext lists) and `NotificationTests.A_new_product_is_announced…` (exact delivery list).
- Adding a subscription to Product changes exact delivery lists (`NotificationTests`) and unfiltered `inventory.pouryakit_receipts` counts (`IntegrationEventTests`); filter by subscription.
- Publishing a second Integration Event type changes the exact delivery list in `NotificationTests.A_new_product_is_announced...`; `IndependentDeliveryTests` already filters by wire name. Any new sample table changes the exact table list in `PersistenceTests.Each_module_has_its_own_schema_and_migrations_history_with_snake_case_names`.

## Counting containers and sharing a fixture (#87, 2026-10-10; xunit.v3 4.0.1, Testcontainers 4.15.0)

- **Testcontainers' `ConsoleLogger` lines never reach `dotnet test`'s output in MTP mode**, even with `--output Detailed`. Run the built test executable with xunit's native filters instead (`bin/Debug/net10.0/ModularMonolith.Tests -class <FQN> -class <FQN> -noColor`): it prints `[testcontainers.org …] Docker container <id> created` per container, and the readiness lines tell the images apart (`pg_isready` for PostgreSQL, `rabbitmqctl add_vhost` for RabbitMQ); the first container is the resource reaper.
- **A collection fixture on xunit.v3 4.0.1:** `[CollectionDefinition(DisableParallelization = true)] public sealed class X : ICollectionFixture<F>;` with `[Collection<X>]` on each class (the generic attribute compiles); `CollectionBehavior.CollectionPerAssembly` still honours it, and a disabled-parallelization collection runs alone. Removing `IClassFixture<F>` keeps the primary-constructor parameter `F`.
- **Moving a private helper into a shared static class strands `using System.Text.Json;`** in each caller that only used `JsonElement` in the helper's signature (IDE0005, five files at once); `using static <Fixtures class>` keeps every call site unchanged.

## XML documentation in the build (#91, 2026-10-10; SDK 10.0.401)

- **CS1591 is active for `src/` only** (`Directory.Build.props`, `NoWarn` conditioned on `IsKitPackage`): a new public member of a Kit package fails the build until it has a `<summary>` or `<inheritdoc />` (the repository's spelling has the space). Samples, tests and template content stay exempt; generated template solutions import `content/shared/Directory.Build.props`, which imports nothing above it.
- **Counting what a red build hides:** `dotnet build PouryaKit.slnx 2>&1 | grep -c CS1591` printed 84, which was 42 errors in `PouryaKit.Domain` printed twice, every downstream project skipped. The full list is `dotnet build PouryaKit.slnx --no-incremental -p:TreatWarningsAsErrors=false`, deduplicated by `path(line,col)`: 179 members in 19 packages. Report both numbers.
- **What CS1591 reports and what it does not:** each enum member, operator, protected constructor and member, and every member inside an extension block, but neither an `extension(...)` block itself nor the primary constructor of a documented type.
- **Inserting many summaries:** a scratchpad script took `(path, warning line, fragment, text)`, asserted the fragment on that line, inserted bottom-up above any attribute lines, kept XML tags whole when wrapping at 120 columns, and refused a spec that differed from the inventory. Then the reviewer's check: `git diff <base> -- src/ | grep '^[-+]' | grep -v '^[-+][-+]' | grep -v '^[-+][[:space:]]*///'` must print nothing.

## Timeouts, hangs and red runs (#99, 2026-10-10; xunit.v3 4.0.1)

- **A rendezvous of two Operations hangs forever when one fails** unless something cancels the wait: `[Fact(Timeout = 30_000)]` plus `WaitAsync(cancellationToken)` on the token the handler receives. At the timeout xUnit cancels `TestContext.Current.CancellationToken` and reports an `AggregateException` holding the test's own failure and `Xunit.Sdk.TestTimeoutException`. The body must name `TestContext.Current.CancellationToken` (xUnit1069), so read it there and pass it down.
- **Showing a hang red:** run `dotnet test` from a scratchpad Python script with `subprocess.Popen(..., start_new_session=True)` and `communicate(timeout=...)`; on `TimeoutExpired`, `os.killpg(process.pid, SIGKILL)` kills the test host too, and `pgrep -fl <assembly>` afterwards shows none left.
- **Undo a red-run mutation with the reverse edit, never `git checkout <file>`** (#155 F3, 2026-10-17): the checkout also drops the file's uncommitted implementation, silently. Mutate with `sed` to a unique marker (`|| false;`) and Edit it back, or commit first.
- **`--filter-method` takes a full name or a wildcard:** a bare method name matches nothing and MTP exits 8 (zero tests); write `*Method_name`.

## Probes and traps (#100, 2026-10-10; SDK 10.0.401)

- **`Host.CreateApplicationBuilder` inside `namespace ModularMonolith.Tests`** resolves `Host` to the sample's `ModularMonolith.Host` namespace (CS0234); write `Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder`. `Record.Exception(() => builder.UsePouryaKitWolverine())` binds the obsolete `Func<Task>` overload (CS0619) because the lambda returns a value; use a statement lambda.
- **A value a factory refuses, for a drift guard's red-once:** `RuntimeHelpers.GetUninitializedObject(typeof(FilterField))` plus `GetField("<Name>k__BackingField", NonPublic | Instance).SetValue` builds a field of a type `FilterField.Of` refuses, when the guard under test sits in another package.
- **`cd <worktree> && python3 - <<'EOF'`** edit scripts of about 70 lines that only read and write files passed; a red-once runner in the scratchpad (`p100/mutate100.py`: JSON spec of replacements or a whole file, restored in `finally`) ran seven mutations without a refusal.

## Testing HostFixture itself (#101, 2026-10-10; Testcontainers 4.15.0, Npgsql 10.0.3)

- A test of the fixture's lifecycle derives a private fixture whose `CreateHost` returns an `IHost` double with `Services = new ServiceCollection().BuildServiceProvider()`: `StartHostAsync` then finds no `DbContextOptions` and only calls `StartAsync`. Call `InitializeAsync` and `DisposeAsync` from the test (a real container, about 1.3 s). The class needs no `AppFixture`, but the assembly fixture still starts with it.
- **Observe the container's disposal by a connection**, with `Pooling = false` on the captured connection string: a pooled idle connector can open after the server is gone. After disposal the open throws an `NpgsqlException`.

## Defaulting a context for one Module in tests (#116, 2026-10-16; SDK 10.0.402)

- **A test-local extension block on `IGatewayProcessor<CatalogModule>` takes the place of `PouryaKit.Testing`'s generic `GatewayTestExtensions` overloads** for every `catalog.SendAsync(cmd, ct)` call: in a file of namespace `ModularMonolith.Tests.Fixtures` it is found in an inner scope first, and from root-namespace files the non-generic receiver wins the tie with the generic one. It made `using Wolverine;` in `BrokerFixture.cs` unnecessary (IDE0005), so check IDE0005 after adding one.
- A Module that starts requiring a tenant broke 90 of 633 tests in `ModularMonolith.Tests`: clients without a tenant header, explicit contexts without a tenant, other Modules' questions continued from a tenantless context, helpers that create a category in the default tenant and a product in a context's tenant (`CategoryNotFound` from the Domain Event Handler, seen as a 30 s delivery timeout), and anonymous calls to a public endpoint that had to become authenticated. Run the whole suite once, list failures per class from the log, fix by file.

## A mutation restore that the build does not see (#117, 2026-10-17; SDK 10.0.402)

- **Restoring a mutated file with `shutil.move(backup, file)` keeps the backup's time, older than the assembly the mutated run built, so the next build skips the project and keeps the mutation.** A later mutation of another project then failed in a way only the earlier one explained. Call `os.utime(file)` after the restore (scratchpad `p117/mutate117.py`), and touch any file restored the old way before trusting a run.
- `if (false)` as a mutation fails CS0162 (warnings are errors), and removing a namespace's only use fails IDE0005: use a predicate that is never true (`key.Count < 0`) and keep the type named.

## Mutations that do not compile (#133, 2026-10-18)

- **A mutation that leaves a primary-constructor parameter unread or a `using` unused fails the build** (CS9113 and IDE0005 are errors here), and a script that reads only the exit code reports it as red. Keep the parameter used (`_ = (unitOfWork, typeof(NpgsqlParameter));`) and have the script report `BUILD FAILED` when the output holds "Build failed" (`scratchpad/p133/mutate133.py`).

**Why:** Test projects run on Microsoft.Testing.Platform, chosen in ticket #4 (2026-10-05) as the plain current .NET 10 idiom; a later ticket repeating VSTest packages would create a second way of running tests. Each other fact cost a build, a run or a vacuous red in tickets #4 to #63 or retro C (2026-10-06).
**How to apply:** before adding a test project, a test filter or an analyzer rule in `.editorconfig`; before every mutation run; when a change adds a table, a Module, a subscription or an Integration Event type to the sample. Related: [[worktree-shell-and-check-gotchas]], [[core-json-and-architecture-test-facts]].
