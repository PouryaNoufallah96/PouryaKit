---
name: aspire-apphost-facts
description: Aspire 13.6.1 AppHost traps (#130, #139 Ollama): the SDK's implicit package and CPM, ASPIRE010, the MessagePack and Fractions floors, what the testing builder's model holds, endpoints, passwords on named volumes, the namespace shadow, validate.py and the skill description limit
metadata:
  type: project
---

Learned building `PouryaKit.Aspire` and `ModularMonolith.AppHost` (#130, 2026-10-18, Aspire 13.6.1).

- **`Aspire.AppHost.Sdk/13.6.1` adds `Aspire.Hosting.AppHost` itself**: a `PackageVersion` for it in `Directory.Packages.props` fails restore with NU1009. It also warns ASPIRE010 (`AspireUseCliBundle=false`, the SDK's default, which takes DCP and the dashboard from NuGet): `NoWarn` it; the AppHost then runs with Docker alone.
- **Aspire.Hosting's closure fails two Kit gates:** `MessagePack` 2.5.302 is NU1902 (GHSA-qhrr-8q5h-9q3h) under warnings-as-errors, and `Fractions` 7.3.0 (via `KubernetesClient` 19.0.2) declares a licence file, which `LicenceTests` refuses. Floors in the package: `MessagePack` 2.5.307, `Fractions` 8.3.2 (MIT expression from 8.x); the AppHost start test passed on 8.3.2. Recorded for the owner, as the Pkcs floor of #124 was.
- **The testing builder's model** (`DistributedApplicationTestingBuilder.CreateAsync<Projects.X>`) holds a hidden `<project>-rebuilder` per project and a `ParameterResource` per `AddParameter`; the generated `<name>-password` parameters are not in it. `WithEnvironment(name, parameter)` adds a `ResourceRelationshipAnnotation` of type `Reference`, so a Host's references include the parameter.
- **A Host project with no `launchSettings.json` gets no endpoint**: `WithHttpEndpoint()`; under the testing builder the Hosts run in `Production`. `WaitForResourceHealthyAsync` on a project with no health check returns once it runs.
- **Named volumes keep the first password**: pass `Parameters:postgres-password=…` (and `cache-`, `broker-`) as builder arguments, or a second run fails against the volume. Volume names are `<apphost>-<hash of the project path>-<resource>-data`, so each worktree gets its own, but a test run and `dotnet run` of one AppHost share them: `dotnet run` persists generated passwords in the AppHost's user secrets, and against a volume the tests initialised its health checks fail and the Hosts wait forever (no error, just no start). The start test therefore swaps each `ContainerMountAnnotation` for one named `<source>-tests` before `BuildAsync`.
- **An AppHost without a launch profile runs in Production and reads no user secrets**, so each `dotnet run` generates and stores new server passwords and the second run fails against its own volume (`28P01`, Hosts wait forever). Ship `Properties/launchSettings.json` with `DOTNET_ENVIRONMENT=Development`. The testing builder replaces the AppHost's `DistributedApplicationOptions` with its own (dashboard off by default), so a sample's own `DisableDashboard` is only testable if the test passes it into the builder's callback.
- **Postgres 18**: Aspire mounts the volume at `/var/lib/postgresql` for an 18.x tag; Aspire's own defaults are the floating `18.3`, `8.6`, `4.3`.
- **Namespace shadow**: inside `namespace PouryaKit.Aspire.Tests`, `Aspire.Hosting…` resolves under `PouryaKit.Aspire`; put the usings at file top or write `global::`.
- **`validate.py`'s sample consumer copies all of `samples/ModularMonolith/src`**, but only `entries` are restored and each entry takes the Host production publish (`codegen write`), so an AppHost is not an entry; the package gets its own consumer under `scripts/packaging/consumers/`.
- **The Kit skill's `description` is near the 1024-character limit** (1012 after #130): a new Capability adds a few words at most.
- **execution-core layout**: a node at (40,170) broke `storages3-cqrs`'s route (`composition/ambiguous-corridor`); (40,560) passed. Check headless screenshots with Playwright's `chrome-headless-shell --headless --screenshot`.

## Ollama in the AppHost (#139, 2026-10-18; Aspire 13.6.1, ollama/ollama:0.40.6)

- **`CommunityToolkit.Aspire.Hosting.Ollama` 13.6.0 is out**: it depends on `OllamaSharp` 5.4.12 (`license type="file"`, the A1 refusal) and on Aspire 13.6.0; check a toolkit package's nuspec dependencies, not only its own licence. A plain `AddContainer` + `VolumeNameGenerator.Generate(builder, "data")` (public in `Aspire.Hosting`) + `WithHttpEndpoint(targetPort: 11434, name: "http")` replaces it.
- **`WaitFor` waits for health checks, not for `ResourceReadyEvent` handlers**: pulling a model on the ready event lets the Host start before its model exists. A keyed `HealthCheckRegistration` in `builder.Services.AddHealthChecks()` plus `WithHealthCheck(key)` that answers `POST /api/show` 200, else pulls with `POST /api/pull {model, stream:false}` inside the check (infinite `HttpClient.Timeout`), holds the Host until the model is served. `builder.Services.AddHttpClient()` compiles in a library on Aspire.Hosting alone.
- **A Host the AppHost runs needs an HTTP route for every proof**: Hosts run as OS processes, so a Gateway-only feature (the agent) needed an endpoint (`POST /catalog/v2/products/ask`); Catalog routes need an API version (`400 Unspecified API Version` without one). A bearer token for the AppHost's Host can be an HS256 JWT built by hand (`HMACSHA256.HashData`, base64url) with `iss`, `aud`, `sub`, `tenant`, `nbf`, `exp`: no IdentityModel package in `PouryaKit.Aspire.Tests`.
- **The AppHost reads `builder.Configuration[...]`**, not `GetValue<T>` (no Binder package): `bool.TryParse`. A plain argument `LocalAgent=true` reaches it through the testing builder's args.
- **qwen2.5:0.5b through the Host answered in about 25 s** with the image and model cached; a run without the endpoint wiring, or without the pull on a fresh volume, returns 500 from the agent.

Related: [[templates-packaging-and-publication-facts]], [[docs-verification-tools]].
