# PouryaKit.Aspire provenance

Adopted implementations in `PouryaKit.Aspire`. Format and rules: [README](README.md).

The package has no adopted implementation. Its registrations, `src/PouryaKit.Aspire/AspireDistributedApplicationBuilderExtensions.cs`, call Aspire's own `AddPostgres`, `AddRedis` and `AddRabbitMQ` with `WithImage` and `WithDataVolume` (ticket #130), and `AddContainer` with `WithVolume` and a health check of the package's own, `src/PouryaKit.Aspire/OllamaModelHealthCheck.cs`, for Ollama (ticket #139). The plan's reference baseline, `CloudNativeKit@e81d65e src/CloudNativeKit.AspireIntegrations/Postgres/PostgresBuilderExtensions.cs:93-189`, only shaped the design, a pinned image with a volume, so it needs no record; its fixed host port, its container name, its proxy switches and its persistent lifetime were left out.

## Package dependencies

Checked on nuget.org on 2026-10-18 (ticket #130); licence expressions from each package's restored nuspec. None is reciprocal or commercial (ADR 0002), and `LicenceTests` checks the whole closure.

- `Aspire.Hosting.PostgreSQL`, `Aspire.Hosting.Redis` and `Aspire.Hosting.RabbitMQ` 13.6.1, `MIT`, the latest stable and the version spec #115 names. They bring `Aspire.Hosting` 13.6.1 and its closure.
- `MessagePack` 2.5.307, `MIT`, a security floor: `Aspire.Hosting` 13.6.1 asks for 2.5.302, which GHSA-qhrr-8q5h-9q3h affects below 2.5.303, and the build's NuGet audit fails on it. 2.5.307 is the latest of the 2.5 line. Delete the reference once `Aspire.Hosting` asks for 2.5.303 or later.
- `Fractions` 8.3.2, `MIT`, a licence-expression floor: `KubernetesClient` 19.0.2, which `Aspire.Hosting` uses to drive its orchestrator, asks for 7.3.0, which ships its licence, BSD-2-Clause, as a file, and `LicenceTests` fails a package without an expression. 8.x declares `MIT`. A major version above what `KubernetesClient` asks for, so its proof is the run: `AppHostTests` starts the AppHost and both Hosts on 8.3.2. Delete the reference once `KubernetesClient` asks for an 8.x release.

- `CommunityToolkit.Aspire.Hosting.Ollama`, the plan's I.3 choice for Ollama, was checked on 2026-10-18 (ticket #139) and not taken: its latest stable, 13.6.0 (`MIT`, on `Aspire.Hosting` 13.6.0), depends on `OllamaSharp` 5.4.12, which ships its licence as a file, the reason spec #115's A1 dropped `OllamaSharp`, and `LicenceTests` would refuse it. The Ollama registration is a plain container instead, and needs no package.

The package's restored closure is 83 packages: 58 `MIT`, 21 `Apache-2.0`, 2 `BSD-3-Clause`, 1 `PostgreSQL` and 1 `Apache-2.0 OR MPL-2.0` (`RabbitMQ.Client`). `PackageGraph` keeps every Aspire package out of every other Kit package's closure. Its private MinVer build reference is recorded in [shared private build tools](README.md#shared-private-build-tools).

## Images

The registrations pin `docker.io/library/postgres:18.6`, `docker.io/library/redis:8.10.1`, `docker.io/library/rabbitmq:4.3.6` and `docker.io/ollama/ollama:0.40.6`, the tags the Kit's Testcontainers fixtures and the Ollama probe run, so local infrastructure is what the tests prove. Aspire 13.6.1's own defaults are the floating `18.3`, `8.6` and `4.3`. The images are infrastructure the AppHost runs, not packages anything references; ADR 0002 governs package dependencies.

## Test dependencies

- `Aspire.Hosting.Testing` 13.6.1, `MIT`, referenced only by `tests/PouryaKit.Aspire.Tests`: `DistributedApplicationTestingBuilder` builds and starts the Proving Sample's `ModularMonolith.AppHost` with the dashboard off.
- `samples/ModularMonolith/src/ModularMonolith.AppHost` uses the `Aspire.AppHost.Sdk` 13.6.1 MSBuild SDK, `MIT`, named in its project file, which adds `Aspire.Hosting.AppHost` 13.6.1, `MIT`, and the orchestrator and dashboard packages for the build's runtime identifier from NuGet, so the AppHost runs with Docker alone and no Aspire CLI or workload.
