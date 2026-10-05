# ModularMonolith provenance

Packages that only the Proving Sample, `samples/ModularMonolith`, references. Format and rules: [README](README.md).

## Package dependencies

Checked on nuget.org on 2026-10-05; licence expressions from each package's nuspec. None is reciprocal or commercial (ADR 0002), and no Kit package references them.

- `Microsoft.Extensions.Hosting` 10.0.12, `MIT`. The Host builds its application with it.
- `Microsoft.EntityFrameworkCore.Design` 10.0.12, `MIT`, referenced by the Host with `PrivateAssets="all"`, so it reaches no other project. `dotnet-ef` needs it in its startup project, the Host, whose services create the Modules' DbContexts. Its closure (`Humanizer.Core`, `Mono.TextTemplating`, `Newtonsoft.Json`, `Microsoft.Build.Framework` and the `Microsoft.CodeAnalysis` packages) is all `MIT`.
- The `dotnet-ef` 10.0.12 tool, `MIT`, pinned in `.config/dotnet-tools.json`. It generates the Modules' migrations and is never referenced at run time.
