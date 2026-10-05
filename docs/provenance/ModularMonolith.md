# ModularMonolith provenance

Packages that only the Proving Sample, `samples/ModularMonolith`, references. Format and rules: [README](README.md).

## Package dependencies

Checked on nuget.org on 2026-10-05; licence expressions from each package's nuspec. None is reciprocal or commercial (ADR 0002), and no Kit package references them.

- `Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.12, `MIT`, checked on 2026-10-05 (ticket #14). The Host authenticates its callers with it. Its closure, the `Microsoft.IdentityModel.*` packages and `System.IdentityModel.Tokens.Jwt` 8.19.2, is all `MIT`. The Host is an ASP.NET Core application (`Microsoft.NET.Sdk.Web`), so it takes its hosting from the shared framework and no longer references `Microsoft.Extensions.Hosting`.
- `Microsoft.AspNetCore.TestHost` 10.0.12, `MIT`, checked on 2026-10-05 (ticket #14), referenced only by `tests/ModularMonolith.Tests`. It serves the Host's HTTP pipeline in memory to the HTTP tests.
- `Microsoft.EntityFrameworkCore.Design` 10.0.12, `MIT`, referenced by the Host with `PrivateAssets="all"`, so it reaches no other project. `dotnet-ef` needs it in its startup project, the Host, whose services create the Modules' DbContexts. Its closure (`Humanizer.Core`, `Mono.TextTemplating`, `Newtonsoft.Json`, `Microsoft.Build.Framework` and the `Microsoft.CodeAnalysis` packages) is all `MIT`.
- The `dotnet-ef` 10.0.12 tool, `MIT`, pinned in `.config/dotnet-tools.json`. It generates the Modules' migrations and is never referenced at run time.
- `WolverineFx.RuntimeCompilation` 6.46.0, `MIT`, checked on 2026-10-05 (ticket #15), referenced by the Host only. Wolverine 6 compiles its runtime code with it, and a Host without it fails at start (ADR 0004, "Defaults chosen", 2); no Kit package references it. It adds `JasperFx.RuntimeCompiler` 5.0.0 and the `Microsoft.CodeAnalysis` packages, all `MIT`. With it and the Kit's messaging packages, the Host's restored closure is 72 packages: 62 `MIT`, 4 `Apache-2.0`, 3 `BSD-3-Clause` and 3 `PostgreSQL`; none is reciprocal or commercial.
