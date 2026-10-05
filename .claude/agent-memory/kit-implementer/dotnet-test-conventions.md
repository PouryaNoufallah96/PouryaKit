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
- `nuget.config` clears sources to nuget.org only: with central package management plus TreatWarningsAsErrors, a second source without mapping turns NU1507 into a restore error.
- CI's setup-dotnet reads global.json; `rollForward: latestPatch` maps to `10.0.4xx`, `latestFeature` would float to any 10.0.
