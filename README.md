# PouryaKit

A reusable .NET 10 foundation for modular monoliths built from vertical slices.

Status: Wave 1 is complete, and Wave 2 is under way. The Kit's fifteen packages, each usable on its own, make up ten Capabilities: domain primitives (`PouryaKit.Domain`); commands, queries and validation (`PouryaKit.Cqrs`, `PouryaKit.Validation`); Modules (`PouryaKit.Modularity`); persistence and the Unit of Work (`PouryaKit.Persistence`, `PouryaKit.Persistence.EFCore`, `PouryaKit.Persistence.Postgres`); Dapper reads (`PouryaKit.Persistence.Dapper`); messaging (`PouryaKit.Messaging`, `PouryaKit.Messaging.Wolverine`, `PouryaKit.Messaging.Wolverine.EFCore`); web (`PouryaKit.Web`); testing (`PouryaKit.Testing`); and, from Wave 2, the three Core helpers (`PouryaKit.Core`): predicate composition, implementing-type lookup and the synchronization-context scope, capped at those three, and security (`PouryaKit.Security`): permissions a command or query declares, checked through the existing authorization hook and never checked again for a trusted continuation, beside the authenticated-by-default HTTP access `PouryaKit.Web` sets, and optional PBKDF2 password hashing with upgrade detection. The Proving Sample in `samples/ModularMonolith/`, a Host of three Modules, proves every guarantee against PostgreSQL, and architecture tests guard its boundaries and every package's dependencies.

The [package validation path](docs/packages.md) builds and tests Release once, packs all fifteen packages at one MinVer version, and verifies them in clean package-only consumers. [CI](docs/ci.md) runs this path with read-only permissions, preserving tested-SHA receipts and bounded package artifacts. Same-repository hosted success/failure and permission isolation are verified; real private fork execution has an owner-accepted verification exception. [Private publication](docs/private-packages.md) downloads trusted main originals, uses standard NuGet push and restores every package of the inventory from the private feed. MinVer supplies snapshots and owner-tagged preview/rc versions through the same validation path. The owner narrowed [Phase 5](docs/foundation-review.md#phase-5-packaging-and-releases) on 2026-10-06; the active scope is complete, with [CI run 37465576160](https://github.com/PouryaNoufallah96/PouryaKit/actions/runs/37465576160) and [publication run 37466084084](https://github.com/PouryaNoufallah96/PouryaKit/actions/runs/37466084084) successful at `b234f4f`. Compatibility automation, named cuts and content-verified recovery in #28/#29 are closed as not planned and require a new scoped ticket. The Capabilities of Wave 2 and later include API versioning and OpenAPI, JWT bearer validation, request idempotency, caching, scheduling, observability, the RabbitMQ transport, multi-tenancy and the project template.

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
