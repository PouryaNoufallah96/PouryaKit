# Package validation and consumption

Run from a clean committed checkout with the SDK in `global.json`, Python 3.11 or later, Git access to origin and Docker running:

```bash
python3 scripts/packaging/validate.py
```

To preserve evidence at a chosen location, pass `--output /absolute/path/to/new-evidence-directory`. Use a fresh directory outside the checkout. The default is a new system temporary directory. The command fetches origin's tags, completes shallow history, evaluates packability, builds Release once with `ContinuousIntegrationBuild=true`, runs the existing tests with `--no-build`, and packs that output with `--no-build`. A failure stops the path before a manifest is written. The command never publishes packages or creates a tag in the Kit repository.

## The package set

[`scripts/packaging/inventory.json`](../scripts/packaging/inventory.json) is the explicit inventory of all thirteen Wave 1 package IDs. The validator evaluates every source, test and sample project against it. Missing projects, extra packable projects, mismatched IDs, duplicate or empty inventory entries and disabled package validation fail. Tests and samples are non-packable by default; the Kit's thirteen source projects are packable.

MinVer 8.0.0 is the only version engine, pinned centrally and private to the build. Its minimum major/minor is `0.1`, tag prefix is `v`, and default prerelease identifiers are `preview.0`. A temporary Git repository proves untagged, exact-tag and post-tag behaviour on each validation. The real package set must agree with MinVer's calculated package/build version. MinVer normalizes `AssemblyVersion` to `major.0.0.0` and `FileVersion` to `major.minor.patch.0`; prerelease identity lives in package and informational versions. See [MinVer's version rules](https://github.com/adamralph/minver/blob/9f485aabb9936efd8fb8b633ae09ae4d0659c94f/README.md#version-numbers).

Each nupkg contains `LICENSE` with the Kit's MIT text and `THIRD-PARTY-NOTICES.md`, repository URL and source commit metadata, and a `net10.0` assembly with an embedded portable PDB. SDK 10 supplies GitHub SourceLink support. Validation reads the PDB's SourceLink map, checks its repository and commit, and checks that it covers the package's source documents. This proves the map in the artifact; it does not prove authenticated downloads from the private GitHub source URL. `EnablePackageValidation` is on for every Kit package. Comparison with a prior named release belongs to the later release-cut work; this path sets no compatibility baseline.

## Clean consumers

The inventory also enumerates the two compositions already supported by Wave 1:

| Consumer | What is built |
|---|---|
| Each of the thirteen package IDs | A separate application compiling a public type from that package, with one direct Kit package reference |
| Single Module | Cqrs, Validation, Postgres persistence, Dapper reads and Web together, as documented in [the Modules guidance](../.agents/skills/pourya-kit/references/modules.md#an-application-of-one-module), without Modularity or Messaging |
| Modular monolith | A copy of the Proving Sample's Host, Modules and Contracts, with every Kit ProjectReference replaced by a package reference |

The Service composition depends on the future RabbitMQ transport and is outside Wave 1. This validation adds no service profile or template.

Every consumer lives outside the checkout, has its own empty NuGet package and HTTP caches, and uses source mapping to resolve `PouryaKit.*` only from the produced local feed. It inherits neither the checkout's build settings nor user feed configuration. Restored assets must name the expected Kit IDs at the exact release version as packages, with no source fallback. Each cached Kit nupkg must have the same SHA-256 as the produced artifact. The modular monolith's ProjectReferences are only between its copied application projects.

`PackageGraph` checks produced nuspecs and each Kit package's reachable restored consumer closure using the same section C reference table as source tests. `LicenceTests` checks third-party licenses in those closures under ADR 0002. Private MinVer is excluded from the source dependency graph but remains in the source license check; a shipped MinVer reference fails the nuspec graph. Negative controls reject missing/extra artifacts, wrong versions and commit metadata, missing notices, forbidden dependencies and invalid symbols. Inventory controls and the focused metadata tests cover the remaining inventory and license cases.

## Consume a verified local set

Use the `packages/` directory printed by the validator as a local NuGet source. Select the version in its `manifest.json`, and use that same version for every Kit PackageReference or central PackageVersion. Keep `nuget.org` available for third-party dependencies. Under package source mapping, map `PouryaKit.*` to the local feed and `*` to nuget.org. The more specific Kit pattern keeps its dependencies in the verified set.

```xml
<PackageReference Include="PouryaKit.Web" Version="0.1.0-preview.0.HEIGHT" />
```

Replace the example version with the manifest's actual MinVer version. Private GitHub Packages consumption and publication are not delivered by this ticket; nuget.org activation and stable 1.0 remain owner decisions.

## Evidence

The output directory retains `build.log`, `runtime-tests.log`, `pack.log`, `evaluated-projects.json`, `package-evidence.json`, individual consumer restore/build logs, `consumer-closures.log`, negative-control logs and `minver.json`. `manifest.json` records the source commit, shared version and each package's filename and SHA-256. Later automation can preserve this directory as the original tested artifact set.

Package restore/build evidence is separate from the Proving Sample's PostgreSQL runtime suite. The latter still executes against the source projects' shared Release output. Consumer builds do not start PostgreSQL, the Host or its deliveries. This command proves local packaging and consumption; hosted PR isolation, private-feed publication, compatibility with a named baseline and recovery require their own evidence.
