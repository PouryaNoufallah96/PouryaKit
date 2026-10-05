# PouryaKit

A reusable .NET 10 foundation for modular monoliths built from vertical slices.

Status: Wave 1 in progress. `PouryaKit.Domain` is usable: Result and Error, typed IDs, entities, value objects, aggregates, Domain Events and business rules. `PouryaKit.Cqrs` and `PouryaKit.Validation` are usable: commands, queries, their handlers and validators, and the dispatcher with its fixed pipeline. `PouryaKit.Modularity`, `PouryaKit.Persistence`, `PouryaKit.Persistence.EFCore`, `PouryaKit.Persistence.Postgres` and `PouryaKit.Testing` are usable for a first Operation end to end: Modules and their Gateways, a DbContext and schema per Module in PostgreSQL, the generic repository, the Unit of Work that commits once, and tests against the hosted application. The Proving Sample in `samples/ModularMonolith/` uses them all. No other Capability is usable yet.

Build and test with the .NET SDK pinned in `global.json`:

```bash
dotnet build PouryaKit.slnx
dotnet test PouryaKit.slnx
```

- Plan and evidence: [docs/foundation-review.md](docs/foundation-review.md)
- Review history: [docs/foundation-review-log.md](docs/foundation-review-log.md)
- Vocabulary: [GLOSSARY.md](GLOSSARY.md)
- Decisions: [docs/adr/](docs/adr/)
