# PouryaKit.Templates provenance

Adopted implementations in `PouryaKit.Templates`. Format and rules: [README](README.md).

The business code the templates generate is the Proving Sample's own, packed from `samples/ModularMonolith/src` (its records are in [ModularMonolith.md](ModularMonolith.md) and the Kit packages' files). The monolith's Composition Root and `IDispatcher` endpoints, the Hosts' `Program.cs` and, since ticket #87, `AppHost.cs`, the smoke tests and the pack-time version step are new Kit code (ticket #53) and have no record here.

## Package dependencies

None shipped: the package holds template content only, with no assembly and no dependency in its nuspec. Its private MinVer build reference is recorded in [shared private build tools](README.md#shared-private-build-tools).

The solutions it generates pin every Kit package at the template package's own version and every third-party package at the version the Kit's `Directory.Packages.props` pins, which `TemplateTests` asserts; each of those packages is recorded with the Kit package or Proving Sample project that already uses it. One package is new to the Kit: `Microsoft.AspNetCore.Mvc.Testing` 10.0.12, `MIT`, the latest stable version on nuget.org, checked against its nuspec on 2026-10-07, ticket #53. The generated smoke tests use its `WebApplicationFactory<Program>`; the Kit's `Directory.Packages.props` pins it so that the templates take its version from there, and no Kit project references it. Since ticket #87 (2026-10-10) the smoke tests run on `PouryaKit.Testing`'s `HostFixture` and serve the Host with `Microsoft.AspNetCore.TestHost` 10.0.12 (`MIT`, recorded in [ModularMonolith.md](ModularMonolith.md)), and the templates neither reference nor pin `Microsoft.AspNetCore.Mvc.Testing`. Since ticket #90 (2026-10-10) the Kit's `Directory.Packages.props` no longer pins it either.

## A template package whose content is a buildable solution

- **Kit files:** `src/PouryaKit.Templates/PouryaKit.Templates.csproj`, `src/PouryaKit.Templates/content/monolith/.template.config/template.json`, `src/PouryaKit.Templates/content/modular/.template.config/template.json`
- **Upstream:** https://github.com/mehdihadeli/vertical-slice-api-template
- **Commit:** 4a398078850cc6bd10f521a09e92c4983cc44955
- **Upstream paths:** `.template.config/template.json`, `vertical-slice-template.nuspec`
- **Licence:** MIT
- **Adoption:** pattern
- **Ticket:** #53
- **Intentional differences:**
  - The template content is the Proving Sample's buildable code, packed from the sample when the package is packed, rather than the whole repository behind an exclude list; only composition-specific files are kept in the package's project.
  - A `sourceName` names the generated solution, as upstream's does, and a source's `exclude` globs leave out the files the monolith replaces, without upstream's boolean symbol.
  - No `Framework` symbol: the Kit targets `net10.0` only, and upstream's symbol replaces a token that appears nowhere.
  - Packed by the `dotnet` client with `PackageType` `Template` and versioned by MinVer with every Kit package, not by a nuspec, `nuget.exe` and nbgv.
  - The package validation path generates, builds and tests each template from the produced package; upstream never builds a generated project.

## Package versions written at pack time, and the restore post-action

- **Kit files:** `src/PouryaKit.Templates/PouryaKit.Templates.csproj`, `src/PouryaKit.Templates/content/monolith/.template.config/template.json`, `src/PouryaKit.Templates/content/monolith/.template.config/dotnetcli.host.json`, `src/PouryaKit.Templates/content/modular/.template.config/template.json`, `src/PouryaKit.Templates/content/modular/.template.config/dotnetcli.host.json`
- **Upstream:** https://github.com/microsoft/aspire
- **Commit:** 3751e615ad660621f2a7aa4c390ef48e08a0eed8
- **Upstream paths:** `src/Aspire.ProjectTemplates/templates/aspire-servicedefaults/.template.config/template.json`, `src/Aspire.ProjectTemplates/templates/aspire-servicedefaults/.template.config/dotnetcli.host.json`, read in the `Aspire.ProjectTemplates` 13.6.1 package, which carries them as `content/templates/aspire-servicedefaults/.template.config/`
- **Licence:** MIT
- **Adoption:** pattern
- **Ticket:** #53
- **Intentional differences:**
  - Upstream bakes each package version into its project files at build; the Kit writes its one MinVer version into each template's `Directory.Packages.props` as `PouryaKitVersion`, with an MSBuild step on `TargetsForTfmSpecificContentInPackage`, and the third-party versions are the Kit's own central versions.
  - `preferNameDirectory`, `primaryOutputs` with the restore post-action, and `skipRestore` as `--no-restore` are taken as upstream has them; the primary output is the generated solution.
  - No `Framework` choice and no bind symbols: the Kit targets `net10.0` only.
