---
name: dotnet-test-conventions
description: Test projects run on Microsoft.Testing.Platform (xunit.v3 4.x = MTP v2); zero tests fails the run; no VSTest packages
metadata:
  type: project
---

Test projects use xunit.v3 4.0.1, which pulls xunit.v3.mtp-v2 (Microsoft.Testing.Platform v2). `global.json` sets `"test": {"runner": "Microsoft.Testing.Platform"}`, so `dotnet test PouryaKit.slnx` (positional path) runs in MTP mode on SDK 10.0.401.

**Why:** chosen in ticket #4 (2026-10-05) as the plain current .NET 10 idiom; a later ticket repeating VSTest packages would create a second way of running tests.

**How to apply:**
- A test csproj is `OutputType Exe`, `IsPackable false`, `PackageReference xunit.v3` (version in Directory.Packages.props), `<Using Include="Xunit" />`. Never add Microsoft.NET.Test.Sdk or xunit.runner.visualstudio.
- MTP fails a test project that runs zero tests (exit code 8), so a new test project needs at least one real test before it joins the solution.
- VSTest flags (`--filter`, `--logger`) make MTP print help and run nothing (exit 5). Filter with `dotnet test --project <csproj> --filter-class <FQN> --filter-method "*Name*" --output Detailed`; `--output Detailed` shows passed tests' output. Say which runner produced a result you report.
- On xunit.v3 4.x, `[assembly: CollectionBehavior(DisableTestParallelization = true)]` is CS0619; use `[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]` to run tests one at a time.
- `nuget.config` clears sources to nuget.org only: with central package management plus TreatWarningsAsErrors, a second source without mapping turns NU1507 into a restore error.
- xUnit analyzer xUnit1051 is an error under TreatWarningsAsErrors: any test call to a method taking a CancellationToken must pass `TestContext.Current.CancellationToken`.
- Ticket #7 (2026-10-05): Cqrs and Validation test projects reference `Microsoft.Extensions.DependencyInjection` (10.0.12, test-only, recorded as a deviation) to build a strict container; logging in tests is an own `ILogger<>` open-generic stand-in or `NullLogger<>`, never `Microsoft.Extensions.Logging`.
- CI's setup-dotnet reads global.json; `rollForward: latestPatch` maps to `10.0.4xx`, `latestFeature` would float to any 10.0.
