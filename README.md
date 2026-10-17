# PouryaKit

A reusable .NET 10 foundation for modular monoliths built from vertical slices.

Status: Wave 1 and Wave 2 are complete. The Wave 2 set is in the private feed since `feat/wave-2` was merged into `main` on 2026-10-09 ([hosted completion](docs/private-packages.md#hosted-completion)). The Kit's packages (every entry of `scripts/packaging/inventory.json`), each usable on its own, make up these Capabilities, plus two features (Paging, Request idempotency) that span several of them:

- [Domain primitives](.agents/skills/pourya-kit/references/domain-primitives.md) (`PouryaKit.Domain`)
- [Commands, queries and validation](.agents/skills/pourya-kit/references/commands-queries-validation.md) (`PouryaKit.Cqrs`, `PouryaKit.Validation`)
- [Modules](.agents/skills/pourya-kit/references/modules.md) (`PouryaKit.Modularity`, with the Host's composition checks in `PouryaKit.Cqrs`)
- [Persistence and the Unit of Work](.agents/skills/pourya-kit/references/persistence.md) (`PouryaKit.Persistence`, `PouryaKit.Persistence.EFCore`, `PouryaKit.Persistence.Postgres`)
- [Dapper reads](.agents/skills/pourya-kit/references/dapper-reads.md) (`PouryaKit.Persistence.Dapper`)
- [Messaging, delayed and recurring commands included](.agents/skills/pourya-kit/references/messaging.md) (`PouryaKit.Messaging`, `PouryaKit.Messaging.Wolverine`, `PouryaKit.Messaging.Wolverine.EFCore`)
- [Web](.agents/skills/pourya-kit/references/web.md) (`PouryaKit.Web`)
- [Testing](.agents/skills/pourya-kit/references/testing.md) (`PouryaKit.Testing`)
- [Core helpers](.agents/skills/pourya-kit/references/core.md) (`PouryaKit.Core`)
- [Security](.agents/skills/pourya-kit/references/security.md) (`PouryaKit.Security`, `PouryaKit.Security.JwtBearer`)
- [Observability](.agents/skills/pourya-kit/references/observability.md) (`PouryaKit.Observability`)
- [Caching](.agents/skills/pourya-kit/references/caching.md) (`PouryaKit.Caching`)
- [Paging](.agents/skills/pourya-kit/references/commands-queries-validation.md#paging) (`PouryaKit.Cqrs`, `PouryaKit.Persistence.Dapper`, `PouryaKit.Persistence.EFCore`, `PouryaKit.Web`)
- [Request idempotency](.agents/skills/pourya-kit/references/commands-queries-validation.md#request-idempotency) (`PouryaKit.Cqrs`, `PouryaKit.Persistence.EFCore`, `PouryaKit.Web`)
- [API versions and OpenAPI](.agents/skills/pourya-kit/references/openapi.md) (`PouryaKit.Web.OpenApi`)
- [Project templates](.agents/skills/pourya-kit/references/templates.md) (`PouryaKit.Templates`)
- [RabbitMQ between Hosts](.agents/skills/pourya-kit/references/rabbitmq.md) (`PouryaKit.Messaging.Wolverine.RabbitMq`)
- [Email](.agents/skills/pourya-kit/references/email.md) (`PouryaKit.Email`, `PouryaKit.Email.MailKit`; Wave 3)
- [Multi-tenancy](.agents/skills/pourya-kit/references/multi-tenancy.md) (`PouryaKit.Domain`, `PouryaKit.Cqrs`, `PouryaKit.Persistence.EFCore`, `PouryaKit.Messaging.Wolverine`; Wave 3, in progress: tenant-scoped Modules, their EF Core filter, their tenant requirement, the save's ownership check, the refusal of a delivery with no tenant, and the composition refusals of a recurring command and a shared Snapshot key, the Dapper tenant rule, and the Dapper lease that binds the tenant)
- [File storage](.agents/skills/pourya-kit/references/storage.md) (`PouryaKit.Storage`, `PouryaKit.Storage.S3`; Wave 3: the contract and its S3 provider)
- [Localization](.agents/skills/pourya-kit/references/localization.md) (`PouryaKit.Localization`; Wave 3, optional: English and Persian)
- [Realtime](.agents/skills/pourya-kit/references/realtime.md) (`PouryaKit.Realtime`; Wave 3, optional: server-sent events from Notification Handlers, heard on every instance through PostgreSQL's notifications)

The skill's [Capability table](.agents/skills/pourya-kit/SKILL.md#capabilities) owns this list and says when to read each reference. It folds Paging and Request idempotency into the rows of the packages they span.

The Proving Sample in `samples/ModularMonolith/` proves every guarantee against PostgreSQL, and against RabbitMQ and Redis where they apply, and architecture tests guard its boundaries and every package's dependencies. Its `ModularMonolith.Host` composes three Modules, or Catalog and Billing alone when `Inventory.Host` receives from it over RabbitMQ. `Inventory.Host` composes the optional [service defaults recipes](.agents/skills/pourya-kit/references/host-recipes.md) of `ModularMonolith.ServiceDefaults` (OpenTelemetry export, liveness and readiness, service discovery, one HTTP resilience pipeline, Serilog), which are template content, not a Kit package.

The [package validation path](docs/packages.md) builds and tests Release once, packs every package in `scripts/packaging/inventory.json` at one MinVer version, and verifies them in clean package-only consumers. [CI](docs/ci.md) runs this path with read-only permissions, preserving tested-SHA receipts and bounded package artifacts. Same-repository hosted success/failure and permission isolation are verified; real private fork execution has an owner-accepted verification exception. [Private publication](docs/private-packages.md) downloads trusted main originals, uses standard NuGet push, then restores every library package and installs the template package from the private feed. MinVer supplies snapshots and owner-tagged preview/rc versions through the same validation path. The owner narrowed [Phase 5](docs/foundation-review.md#phase-5-packaging-and-releases) on 2026-10-06; the active scope is complete, and [Hosted completion](docs/private-packages.md#hosted-completion) records the CI and publication runs that proved it, and those that proved Wave 2. Wave 3 has begun with multi-tenancy; the Capabilities still to come include the rest of Wave 3 and the service template.

Build and test with the .NET SDK pinned in `global.json`:

```bash
dotnet build PouryaKit.slnx
dotnet test PouryaKit.slnx
```

With Docker running, validate the Release package set, PostgreSQL suite and clean consumers:

```bash
python3 scripts/packaging/validate.py
```

The command prints its evidence directory outside the checkout, including the packages, checksum manifest and separate runtime and consumer logs. It fetches origin's tags and completes shallow history before MinVer runs. [Package guidance](docs/packages.md) describes consumption and the proof boundary.

- Using the Kit, Capability by Capability: the Kit's skill, [.agents/skills/pourya-kit/](.agents/skills/pourya-kit/SKILL.md)
- Packages, their references and the commit sequence: [docs/architecture/README.md](docs/architecture/README.md)
- Diagrams of the Kit's structure, runtime flows and publication, in reading order: [the architecture document's Diagrams section](docs/architecture/README.md#diagrams)
- Which test proves each acceptance scenario: [docs/acceptance.md](docs/acceptance.md)
- Decisions: [docs/adr/](docs/adr/)
- Plan and evidence: [docs/foundation-review.md](docs/foundation-review.md)
- Review history: [docs/foundation-review-log.md](docs/foundation-review-log.md)
- Vocabulary: [GLOSSARY.md](GLOSSARY.md)
- Provenance of adopted code: [docs/provenance/](docs/provenance/README.md)
