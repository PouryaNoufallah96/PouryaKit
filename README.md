# PouryaKit

A reusable .NET 10 foundation for modular monoliths built from vertical slices.

Status: Wave 1 is complete, and Wave 2 is under way. The Kit's packages (every entry of `scripts/packaging/inventory.json`), each usable on its own, make up these Capabilities:

- domain primitives (`PouryaKit.Domain`)
- commands, queries and validation (`PouryaKit.Cqrs`, `PouryaKit.Validation`)
- Modules (`PouryaKit.Modularity`), whose Composition Roots name neither their Module nor their assembly from Wave 2, with the Host's composition checks (`PouryaKit.Cqrs`), which stop a Host when it is built, before anything runs, on a mistake the Kit can see: a Module's declaration made without its Module, a declared permission without its check, a cacheable query without caching; a replaced container stops it when it starts
- persistence and the Unit of Work (`PouryaKit.Persistence`, `PouryaKit.Persistence.EFCore`, `PouryaKit.Persistence.Postgres`)
- Dapper reads (`PouryaKit.Persistence.Dapper`)
- messaging (`PouryaKit.Messaging`, `PouryaKit.Messaging.Wolverine`, `PouryaKit.Messaging.Wolverine.EFCore`), with, from Wave 2, delayed commands that continue the Operation that scheduled them and recurring commands declared under a named system Actor, both durable through a restart
- web (`PouryaKit.Web`)
- testing (`PouryaKit.Testing`)
- from Wave 2, the three Core helpers (`PouryaKit.Core`): predicate composition, implementing-type lookup and the synchronization-context scope, capped at those three
- from Wave 2, security (`PouryaKit.Security`): permissions a command or query declares, checked through the existing authorization hook and never checked again for a trusted continuation, beside the authenticated-by-default HTTP access `PouryaKit.Web` sets, which also sends the Operation Context on outgoing HTTP with the standard retry and lets configured, authenticated peer Hosts continue an Operation, and applies configured CORS policies, response compression and a rate limit per Actor whose rejection is a 429 problem response, optional PBKDF2 password hashing with upgrade detection, and optional JWT bearer authentication (`PouryaKit.Security.JwtBearer`): standard ASP.NET Core bearer validation whose issuer, audience, signature and lifetime checks cannot be turned off, with configured token claims normalized into the Actor and tenant
- observability (`PouryaKit.Observability`): one activity and one duration in fractional seconds for every command and query, tagged with its Module, operation, Actor kind and outcome, and a delivery's Operation that continues the publishing Operation's trace and tracestate through the Outbox, inside one Host and across RabbitMQ into another, with exporters left to the Host
- caching (`PouryaKit.Caching`): a query's successes cached per Actor and tenant over `HybridCache`, never a failure and only as the Operation's outermost query, never nested in another query, inside its checks or a write Unit of Work, with the cache tags its commands declare evicted once an Operation commits, and Redis as the application's optional shared cache
- paging with allow-listed sorting and filtering: one `PageRequest` and `Page` contract in `PouryaKit.Cqrs`, read through `PouryaKit.Persistence.Dapper` or `PouryaKit.Persistence.EFCore` and bound from the query string by `PouryaKit.Web`
- request idempotency: a command that opts in, repeated with the same request id, over HTTP the `Idempotency-Key` its endpoint requires, replays its first success from a key reserved in its own transaction and kept for a configured retention, 24 hours by default, never running or publishing twice (`PouryaKit.Cqrs`, `PouryaKit.Persistence.EFCore`, `PouryaKit.Web`)
- the optional API versions and OpenAPI (`PouryaKit.Web.OpenApi`): versions a Module declares, served on its route group by URL segment and by `api-version` header, a deprecated version's configured sunset, one OpenAPI document per supported version with each operation's security requirement, and the Scalar reference
- the project templates (`PouryaKit.Templates`): `dotnet new pouryakit-monolith` and `dotnet new pouryakit-modular-monolith` generate a solution from the Proving Sample's business code, Catalog as a monolith without Modularity or the three Modules as a modular monolith, with its own Composition Roots and HTTP endpoints, the service defaults recipes, the Kit's packages at the version the templates were packed with, and an HTTP smoke test; each Host pre-generates Wolverine's code with `dotnet run -- codegen write` and loads it outside Development, so a Release publish carries no runtime compiler; the package validation path generates, builds and tests both, and checks each Host's Release publish
- the optional RabbitMQ transport (`PouryaKit.Messaging.Wolverine.RabbitMq`): Integration Events and Snapshots delivered between Hosts through one exchange per wire name and one durable queue per Host, fanned out to each subscription's own queue, so Receipts, retries, dead letters, Operation Context and versions hold as inside one Host, and a body the receiving contract cannot read, a required member missing or null where it is non-nullable, is dead-lettered instead of handled with defaults

The Proving Sample in `samples/ModularMonolith/`, a Host of three Modules, or of Catalog and Billing beside `Inventory.Host` over RabbitMQ, which composes the optional service defaults recipes of `ModularMonolith.ServiceDefaults` (OpenTelemetry export, liveness and readiness, service discovery, one HTTP resilience pipeline, Serilog), template content and not a Kit package, proves every guarantee against PostgreSQL, and RabbitMQ and Redis where they apply, and architecture tests guard its boundaries and every package's dependencies.

The [package validation path](docs/packages.md) builds and tests Release once, packs every package in `scripts/packaging/inventory.json` at one MinVer version, and verifies them in clean package-only consumers. [CI](docs/ci.md) runs this path with read-only permissions, preserving tested-SHA receipts and bounded package artifacts. Same-repository hosted success/failure and permission isolation are verified; real private fork execution has an owner-accepted verification exception. [Private publication](docs/private-packages.md) downloads trusted main originals, uses standard NuGet push and restores every package of the inventory from the private feed. MinVer supplies snapshots and owner-tagged preview/rc versions through the same validation path. The owner narrowed [Phase 5](docs/foundation-review.md#phase-5-packaging-and-releases) on 2026-10-06; the active scope is complete, with [CI run 37465576160](https://github.com/PouryaNoufallah96/PouryaKit/actions/runs/37465576160) and [publication run 37466084084](https://github.com/PouryaNoufallah96/PouryaKit/actions/runs/37466084084) successful at `b234f4f`. Compatibility automation, named cuts and content-verified recovery in #28/#29 are closed as not planned and require a new scoped ticket. The Capabilities of later Waves include multi-tenancy and the service template.

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
- Which test proves each acceptance scenario: [docs/acceptance.md](docs/acceptance.md)
- Decisions: [docs/adr/](docs/adr/)
- Plan and evidence: [docs/foundation-review.md](docs/foundation-review.md)
- Review history: [docs/foundation-review-log.md](docs/foundation-review-log.md)
- Vocabulary: [GLOSSARY.md](GLOSSARY.md)
- Provenance of adopted code: [docs/provenance/](docs/provenance/README.md)
