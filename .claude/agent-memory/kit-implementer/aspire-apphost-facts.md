---
name: aspire-apphost-facts
description: Aspire 13.6.1 AppHost traps (#130): the SDK's implicit package and CPM, ASPIRE010, the MessagePack and Fractions floors, what the testing builder's model holds, endpoints, passwords on named volumes, the namespace shadow, validate.py and the skill description limit
metadata:
  type: project
---

Learned building `PouryaKit.Aspire` and `ModularMonolith.AppHost` (#130, 2026-10-18, Aspire 13.6.1).

- **`Aspire.AppHost.Sdk/13.6.1` adds `Aspire.Hosting.AppHost` itself**: a `PackageVersion` for it in `Directory.Packages.props` fails restore with NU1009. It also warns ASPIRE010 (`AspireUseCliBundle=false`, the SDK's default, which takes DCP and the dashboard from NuGet): `NoWarn` it; the AppHost then runs with Docker alone.
- **Aspire.Hosting's closure fails two Kit gates:** `MessagePack` 2.5.302 is NU1902 (GHSA-qhrr-8q5h-9q3h) under warnings-as-errors, and `Fractions` 7.3.0 (via `KubernetesClient` 19.0.2) declares a licence file, which `LicenceTests` refuses. Floors in the package: `MessagePack` 2.5.307, `Fractions` 8.3.2 (MIT expression from 8.x); the AppHost start test passed on 8.3.2. Recorded for the owner, as the Pkcs floor of #124 was.
- **The testing builder's model** (`DistributedApplicationTestingBuilder.CreateAsync<Projects.X>`) holds a hidden `<project>-rebuilder` per project and a `ParameterResource` per `AddParameter`; the generated `<name>-password` parameters are not in it. `WithEnvironment(name, parameter)` adds a `ResourceRelationshipAnnotation` of type `Reference`, so a Host's references include the parameter.
- **A Host project with no `launchSettings.json` gets no endpoint**: `WithHttpEndpoint()`; under the testing builder the Hosts run in `Production`. `WaitForResourceHealthyAsync` on a project with no health check returns once it runs.
- **Named volumes keep the first password**: pass `Parameters:postgres-password=…` (and `cache-`, `broker-`) as builder arguments, or a second run fails against the volume. Volume names are `<apphost>-<hash of the project path>-<resource>-data`, so each worktree gets its own.
- **Postgres 18**: Aspire mounts the volume at `/var/lib/postgresql` for an 18.x tag; Aspire's own defaults are the floating `18.3`, `8.6`, `4.3`.
- **Namespace shadow**: inside `namespace PouryaKit.Aspire.Tests`, `Aspire.Hosting…` resolves under `PouryaKit.Aspire`; put the usings at file top or write `global::`.
- **`validate.py`'s sample consumer copies all of `samples/ModularMonolith/src`**, but only `entries` are restored and each entry takes the Host production publish (`codegen write`), so an AppHost is not an entry; the package gets its own consumer under `scripts/packaging/consumers/`.
- **The Kit skill's `description` is near the 1024-character limit** (1012 after #130): a new Capability adds a few words at most.
- **execution-core layout**: a node at (40,170) broke `storages3-cqrs`'s route (`composition/ambiguous-corridor`); (40,560) passed. Check headless screenshots with Playwright's `chrome-headless-shell --headless --screenshot`.

Related: [[templates-packaging-and-publication-facts]], [[docs-verification-tools]].
