# PouryaKit

A reusable .NET 10 foundation for modular monoliths built from vertical slices.

Status: Wave 1 in progress. `PouryaKit.Domain` is usable: Result and Error, typed IDs, entities, value objects, aggregates, Domain Events and business rules. `PouryaKit.Cqrs` and `PouryaKit.Validation` are usable: commands, queries, their handlers and validators, and the dispatcher with its fixed pipeline. `PouryaKit.Modularity`, `PouryaKit.Persistence`, `PouryaKit.Persistence.EFCore`, `PouryaKit.Persistence.Postgres` and `PouryaKit.Testing` are usable for a first Operation end to end: Modules and their Gateways, a DbContext and schema per Module in PostgreSQL, the generic repository, the Unit of Work that commits once, and tests against the hosted application. Domain Event Handlers react inside that Unit of Work, before it commits, and a Module names its specifications and extends the generic repository with an aggregate-specific one where it needs to. Every Gateway call runs with an Operation Context that names its Actor, a Module answers another Module's question through its Contracts and its Gateway, and the Host refuses a miswired Module or container at startup. `PouryaKit.Persistence.Dapper` is usable: reads in plain SQL with typed IDs as parameters and results, and a plain-SQL write to a Module-internal table inside the Unit of Work. The Unit of Work also fills audit fields, keeps a removed soft-deletable aggregate marked deleted, advances each changed aggregate's version once, and fails an Operation with an Error on a concurrent change or on a duplicate the Module's unique index refuses. `PouryaKit.Web` is usable: an endpoint beside each Feature, one problem response for every failure, and the Operation Context built from the authenticated caller. `PouryaKit.Messaging`, `PouryaKit.Messaging.Wolverine` and `PouryaKit.Messaging.Wolverine.EFCore` are usable for Notification Handlers: the Unit of Work stores each Notification in the Outbox in the same commit as the change, and each handler runs after the commit as a continuation of the Operation, retried and recorded on its own with a Receipt. They are usable for Integration Events between Modules too: a Notification Handler publishes one, carrying its Domain Event's identity, into the Outbox in the commit of its own delivery, and the receiving Module's Integration Event Handler applies it once, its effect and Receipt committed together. Several Modules may subscribe to one Integration Event, each delivery retried on its own, and a delivery that keeps failing is set aside as a dead letter of its subscription, never marked handled, to inspect and replay; `PouryaKit.Testing` waits for and asserts the deliveries an Operation caused, and reads and replays its dead letters. The Proving Sample in `samples/ModularMonolith/` uses them all. No other Capability is usable yet.

Build and test with the .NET SDK pinned in `global.json`:

```bash
dotnet build PouryaKit.slnx
dotnet test PouryaKit.slnx
```

- Plan and evidence: [docs/foundation-review.md](docs/foundation-review.md)
- Review history: [docs/foundation-review-log.md](docs/foundation-review-log.md)
- Vocabulary: [GLOSSARY.md](GLOSSARY.md)
- Decisions: [docs/adr/](docs/adr/)
