# Package validation and consumption

Run from a clean committed checkout with the SDK in `global.json`, Python 3.11 or later, Git access to origin and Docker running:

```bash
python3 scripts/packaging/validate.py
```

To preserve evidence at a chosen location, pass `--output /absolute/path/to/new-evidence-directory`. Use a fresh directory outside the checkout: one that already holds files is refused before any work unless `--keep` is passed, since a PASS removes the directory with everything in it. The default is a new system temporary directory. The command fetches origin's tags, completes shallow history, evaluates packability, builds Release once with `ContinuousIntegrationBuild=true`, runs the existing tests with `--no-build`, and packs that output with `--no-build`. A failure stops the path before a manifest is written. The command never publishes packages or creates a tag in the Kit repository.

CI passes `--no-fetch` after authenticated checkout has fetched full history and tags and removed its credentials. This option rejects shallow repositories and uses the checkout's history without network Git access. `history.json` records fetch mode, source SHA and available tags. Use it only when the caller has already fetched the complete history and tags; see [CI guidance](ci.md).

## The package set

[`scripts/packaging/inventory.json`](../scripts/packaging/inventory.json) is the explicit inventory of every Kit package ID; `PouryaKit.Templates` is marked `"kind": "template"`. The validator evaluates every source, test and sample project against it, but the projects under `src/PouryaKit.Templates/content`, which are template content. Missing projects, extra packable projects, mismatched IDs, duplicate or empty inventory entries and disabled package validation fail. Tests and samples are non-packable by default; the Kit's source projects, one per inventory package, are packable.

MinVer 8.0.0 is the only version engine, pinned centrally and private to the build. Its minimum major/minor is `0.1`, tag prefix is `v`, and default prerelease identifiers are `preview.0`. A temporary Git repository proves untagged, exact-tag and post-tag behaviour on each validation. The real package set must agree with MinVer's calculated package/build version. MinVer normalizes `AssemblyVersion` to `major.0.0.0` and `FileVersion` to `major.minor.patch.0`; prerelease identity lives in package and informational versions. See [MinVer's version rules](https://github.com/adamralph/minver/blob/9f485aabb9936efd8fb8b633ae09ae4d0659c94f/README.md#version-numbers).

Each nupkg contains `LICENSE` with the Kit's MIT text, repository URL and source commit metadata, its project's `README.md`, the shared `icon.png` (`src/icon.png`), its project's own `PackageDescription`, held by equality, its tags, exactly the three every package shares and its project's own `PackageTags`, compared as a set, each once, and, but for the template package, which holds content only, a `net10.0` assembly with an embedded portable PDB and its XML documentation file, so a consumer reads the Kit's summaries in the editor. SDK 10 supplies GitHub SourceLink support. Validation reads the PDB's SourceLink map, checks its repository and commit, expands exact and wildcard mappings in order of specificity, and compares every package source document's mapped URL with its repository-relative path. A real embedded-PDB control with the correct repository/commit and a bogus file path must fail. This proves the map in the artifact; it does not prove authenticated downloads from the private GitHub source URL. `EnablePackageValidation` is on for every Kit package. This path sets no compatibility baseline.

## Clean consumers

The inventory also enumerates the compositions. A template consumer names its template and, as `generated`, the name its solution is generated under; the validation generates, builds, smoke-tests and publishes exactly the templates the inventory's consumers name, and finds each generated solution's Host and test project by that name (`<name>.Host` and `<name>.Tests`), so a template added to the inventory needs no change to the script.

| Consumer | What is built |
|---|---|
| Each library package ID in the inventory | A separate application compiling a public type from that package, with one direct Kit package reference |
| Single Module | The registrations and explicit Operation scope of [the Modules guidance](../.agents/skills/pourya-kit/references/modules.md#an-application-of-one-module), with Cqrs's Host entry `AddPouryaKitCompositionChecks`, using Cqrs, Validation, Postgres persistence, Dapper reads and Web without Modularity or Messaging |
| Modular monolith | A copy of the Proving Sample's two Hosts (`ModularMonolith.Host` and `Inventory.Host`), its `ModularMonolith.ServiceDefaults` recipe project, Modules and Contracts, with every Kit ProjectReference replaced by a package reference; each Host then takes the template Hosts' production step on the copy: `codegen write`, a Release restore and publish, and the same publish check (ADR 0004) |
| Template monolith | `dotnet new pouryakit-monolith -n Acme.Shop` from the produced `PouryaKit.Templates`, installed into a template hive of its own (`--debug:custom-hive`), restored with only the consumer's `NuGet.Config` and built as generated, its own `global.json`, build settings and central versions included; `PouryaKit.Modularity` and `PouryaKit.Testing` are refused in the closure of every project but the generated test project, which restores `PouryaKit.Testing` and, through it, Modularity (ticket #87), and its HTTP smoke test runs; then `codegen write` runs on its Host from the Release build, the Host is restored for Release and published, and the publish must hold the Host built with the generated code and neither `Wolverine.RuntimeCompilation.dll` nor `JasperFx.RuntimeCompiler.dll` (ADR 0004), nor an assembly of a refused package |
| Template modular monolith | `dotnet new pouryakit-modular-monolith -n acme-shop`, the same way, with a name a namespace cannot hold, so the generated solution's paths carry it as typed and its code `acme_shop`; `PouryaKit.Testing` is refused outside its test project, its smoke test runs, and its Host's Release publish is checked the same way |
| Template service | `dotnet new pouryakit-service`, the same way, as `-n Acme.Stock` and as `-n acme-stock`, two consumers: one Module, Inventory, with `Catalog.Contracts` and no Reverse proxy; `PouryaKit.Testing` and `PouryaKit.ReverseProxy` are refused outside its test project, its smoke tests run against PostgreSQL and RabbitMQ in containers, and its Host's Release publish is checked the same way |

The Service composition, one Module in its own process over RabbitMQ, is the template consumer above, `pouryakit-service` (ticket #171), generated from the business code that `Inventory.Host`, Wave 2's broker proof Host, runs; this validation also builds that Host from packages beside `ModularMonolith.Host` ([the project templates](../.agents/skills/pourya-kit/references/templates.md)).

Every consumer lives outside the checkout, has its own empty NuGet package and HTTP caches in a sibling directory outside its source globs, and uses source mapping to resolve `PouryaKit.*` only from the produced local feed. It inherits neither the checkout's build settings nor user feed configuration. Restored assets must name the expected Kit IDs at the exact release version as packages, with no source fallback. Each cached Kit nupkg must have the same SHA-256 as the produced artifact. The modular monolith's ProjectReferences are only between its copied application projects.

`PackageGraph` checks produced nuspecs and each Kit package's reachable restored consumer closure using the same section C reference table as source tests. `LicenceTests` checks third-party licenses in those closures under ADR 0002. Private MinVer is excluded from the source dependency graph but remains in the source license check; a shipped MinVer reference fails the nuspec graph. Negative controls reject missing/extra artifacts, wrong versions and commit metadata, forbidden direct and restored transitive dependencies, and invalid symbols. The transitive control adds Npgsql under WolverineFx in real consumer assets and must fail specifically on the transport-neutral package's Npgsql exclusion. Inventory controls and the focused metadata tests cover the remaining inventory and license cases.

## Consume a verified local set

Run the validator with `--keep`, since after a PASS it removes its output directory otherwise, and use the `packages/` directory it prints as a local NuGet source. Select the version in its `manifest.json`, and use that same version for every Kit PackageReference or central PackageVersion. Keep `nuget.org` available for third-party dependencies. Under package source mapping, map `PouryaKit.*` to the local feed and `*` to nuget.org. The more specific Kit pattern keeps its dependencies in the verified set.

```xml
<PackageReference Include="PouryaKit.Web" Version="0.1.0-preview.0.HEIGHT" />
```

Replace the example version with the manifest's actual MinVer version. The shared validator never publishes; the delivered [private publisher](private-packages.md) handles GitHub Packages publication and consumption. After its push it restores every library package from the feed and installs `PouryaKit.Templates` from it rather than referencing it, then generates and restores the solution of each template; the Wave 2 set publishes with the merge of `feat/wave-2` to `main`, the only branch that publishes. `0.1.0-preview.0.28` is also on nuget.org, published once by hand ([nuget.org publication](private-packages.md#nugetorg-publication)); no workflow publishes there, and stable 1.0 remains an owner decision.

## Evidence

With `--keep`, the output directory retains `build.log`, `runtime-tests.log`, `pack.log`, `evaluated-projects.json`, `package-evidence.json`, `template-install.log`, individual consumer restore/build logs and each template consumer's smoke test, `codegen write` and publish logs, `consumer-closures.log`, negative-control logs and `minver.json`. `manifest.json` records the source commit, shared version and each package's filename and SHA-256. [CI](ci.md) preserves this directory as the original tested artifact set for the [private publisher](private-packages.md).

Package restore/build evidence is separate from the Proving Sample's PostgreSQL runtime suite. The latter still executes against the source projects' shared Release output. Consumer builds do not start PostgreSQL, the Host or its deliveries, but for the template consumers: each generated solution's smoke test hosts its generated Host in memory against PostgreSQL and, for the service, RabbitMQ in containers, so the validation needs Docker. This command proves local packaging and consumption; hosted PR isolation, private-feed publication, compatibility with a named baseline and recovery require their own evidence.

## The composition proof

The smoke tests host each generated Host alone, in memory. `scripts/packaging/composition_proof.py` runs two of them together as processes, on the output directory of a validation kept with `--keep`, the way [`.agents/scripts/ci-local.sh`](../.agents/scripts/ci-local.sh) keeps it too (ticket #172):

```bash
python3 scripts/packaging/validate.py --keep --output /absolute/path/to/new-evidence-directory
python3 scripts/packaging/composition_proof.py /absolute/path/to/new-evidence-directory
```

It takes the inventory's `pouryakit-modular-monolith` and `pouryakit-service` consumers, starts `postgres:18.6` and `rabbitmq:4.3.6` in containers on free loopback ports, and migrates a database of its own for each solution with `dotnet tool restore` and `dotnet ef database update --configuration Release --no-build` per EF Core Module, with the consumer's isolated caches. It runs each Host's Release publish in Production with the same bearer issuer, audience and signing key, the service first, and waits until each Host's queue, the template's derived service name, has its consumer on the broker. It then creates a category and a product in the tenant `north` through the modular monolith's Catalog with a token it signs, and asks the service's `GetStockItem` until it answers 200 with that product's id, within 60 seconds. It prints one `PASS composition proof` line naming both generated solutions, the queues and the stock item observed, or a `FAIL` line with the last answer, and stops both Hosts and removes both containers with their anonymous volumes either way; each step's output is in `<output>/composition-proof/`. A wrong wire name, queue name or missing subscription in generated content fails it. It is a recorded step, not a step of the validation, which neither runs nor needs it; its own token, inputs and PASS line have unit tests in `scripts/packaging/test_composition_proof.py`.
