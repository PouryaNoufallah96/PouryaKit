---
name: template-package-facts
description: Ticket #53 facts - dotnet new engine traps, pack-time version step, WebApplicationFactory in generated tests, NuGet PrivateAssets graph gap, global-cache pollution from probes
metadata:
  type: project
---

Verified in ticket #53 (2026-10-07), SDK 10.0.401, ASP.NET Core 10.0.12:

- **Template engine.** A source outside the template's install root fails (`MV012`), so the sample is packed into the same package (`content/sample/`) and each `template.json` points at `../sample/`. `--debug:custom-hive` needs an absolute path; a relative one fails with "Template package location … is not supported". `sourceName` is the sample's own dotless `ModularMonolith`, so packed Host files' `namespace ModularMonolith.Host` become `Acme.Shop.Host` with no copy; an identifier containing the token (`ModularMonolithHost`) would become `Acme.ShopHost`, so template content avoids that. The restore post-action works on a `.slnx` primary output.
- **Pack-time version.** NuGet's `GenerateNuspecDependsOn` runs `_GetPackageFiles` before MinVer; a target on `TargetsForTfmSpecificContentInPackage` with `DependsOnTargets="MinVer"` sees `$(PackageVersion)` and adds `TfmSpecificPackageFile` items, also under `dotnet pack --no-build`. `Directory.Build.targets` overrides `Description`; set `PackageDescription`.
- **Repository checks a template package breaks:** `Sample.KitPackages` does `Assembly.Load` per `src/` folder (skip it), `PackageArtifacts` SourceLink needs a dll (skip by nuspec `packageType` Template), `PackageGraph` needs a row, `validate.py` would evaluate template-content csproj files as packable through the root `Directory.Build.props` (skip `src/PouryaKit.Templates/content`). A `PackageReference` to a Template package restores, so the publication restore consumer needs no change.
- **WebApplicationFactory<Program> in a generated test:** `Program` is public by itself in .NET 10; `UseSetting` values are visible to `builder.Configuration` reads before `Build()` (Wolverine's connection string); two test classes calling `CreateClient` at once start two Hosts and race migrations, so start it in `InitializeAsync` (`_ = Services`). Migrate in a `CreateHost` override before `host.Start()`. `WithWebHostBuilder(ConfigureTestServices(...))` then `.Services` throws the Host's build exception as is.
- **NuGet graph gap:** a Host's direct `Microsoft.EntityFrameworkCore.Design` with `PrivateAssets="all"` drops that package from a referencing test project's graph although `WolverineFx.EntityFrameworkCore` depends on it; `RestoredPackage.InConsumer` now skips a dependency the graph left out (first validate run failed on it).
- **Global cache pollution:** `dotnet ef`, `dotnet run` or a `dotnet new` restore post-action on a generated solution under a folder whose `NuGet.Config` names a probe feed, without `NUGET_PACKAGES` set, puts dev `PouryaKit.*` versions (height-based, can collide with a later published one) into `~/.nuget/packages`. Remove them file by file after checking `.nupkg.metadata`'s `source` is the probe feed.
- `WebApplicationBuilder.ConfigureContainer` is an explicit interface member: cast to `IHostApplicationBuilder`. Its `configure` callback sees the final collection, which is how a Host without Modularity runs `EnsurePouryaKitDeclaredAuthorizers()`.
- `validate.py` with both template consumers: about 7.3 min, the template consumers about 85 s of it.

**Why:** each cost a probe or a validation run in #53.
**How to apply:** for #54 close-out, a third (service) template in Wave 4, or any package that is not a library. Related: [[core-helpers-facts]], [[declared-authorizer-check-facts]], [[cross-process-tracing-and-recipes-facts]].
