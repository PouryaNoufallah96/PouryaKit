# PouryaKit

A reusable .NET 10 foundation for modular monoliths built from vertical slices.

Status: Wave 1 is complete. Its thirteen packages, each usable on its own, make up eight Capabilities: domain primitives (`PouryaKit.Domain`); commands, queries and validation (`PouryaKit.Cqrs`, `PouryaKit.Validation`); Modules (`PouryaKit.Modularity`); persistence and the Unit of Work (`PouryaKit.Persistence`, `PouryaKit.Persistence.EFCore`, `PouryaKit.Persistence.Postgres`); Dapper reads (`PouryaKit.Persistence.Dapper`); messaging (`PouryaKit.Messaging`, `PouryaKit.Messaging.Wolverine`, `PouryaKit.Messaging.Wolverine.EFCore`); web (`PouryaKit.Web`); and testing (`PouryaKit.Testing`). The Proving Sample in `samples/ModularMonolith/`, a Host of three Modules, proves every guarantee against PostgreSQL, and architecture tests guard its boundaries and every package's dependencies.

Not in Wave 1: packaging and publishing the packages, which is Phase 5, and the Capabilities of Wave 2 and later, such as API versioning and OpenAPI, security, request idempotency, caching, scheduling, observability, the RabbitMQ transport, multi-tenancy and the project template.

Build and test with the .NET SDK pinned in `global.json`:

```bash
dotnet build PouryaKit.slnx
dotnet test PouryaKit.slnx
```

- Using the Kit, Capability by Capability: the Kit's skill, [.agents/skills/pourya-kit/](.agents/skills/pourya-kit/SKILL.md)
- Packages, their references and the commit sequence: [docs/architecture/README.md](docs/architecture/README.md)
- Which test proves each acceptance scenario: [docs/acceptance.md](docs/acceptance.md)
- Decisions: [docs/adr/](docs/adr/)
- Plan and evidence: [docs/foundation-review.md](docs/foundation-review.md)
- Review history: [docs/foundation-review-log.md](docs/foundation-review-log.md)
- Vocabulary: [GLOSSARY.md](GLOSSARY.md)
- Provenance of adopted code: [docs/provenance/](docs/provenance/README.md)
