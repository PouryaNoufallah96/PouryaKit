---
name: pourya-kit
description: How to build a .NET modular monolith on PouryaKit. Use when writing, reviewing or testing code in a project that references PouryaKit packages, such as a Module, an aggregate, a command or query, a Domain Event Handler, a Dapper read, an Integration Event Handler or an endpoint.
---

# PouryaKit

PouryaKit lets Feature code hold only business behaviour. The Feature supplies the aggregate, the command or query, its validator and handler, its data access and its endpoint. The Kit dispatches the Operation, validates it, owns its single commit, runs its reactions and delivers its Integration Events and Snapshots to other Modules.

These hold in every Capability:

- A handler returns a Result. An expected business failure is an Error in that Result; exceptions are for bugs and infrastructure.
- The Unit of Work saves, commits and writes the Outbox. Feature code returns its Result and leaves all three to it.
- Anything outside a Module reaches it through its Gateway.
- Names follow the Kit's `GLOSSARY.md`: Module, Feature, Operation, Domain Event, Notification Handler, Integration Event, Snapshot.

## Capabilities

Each section below is filled by the ticket that delivers its Capability. A section that reads *Not yet available* has no API yet: work from the ticket it names, not from the package name.

### Domain primitives

`PouryaKit.Domain`: Result and Error, typed IDs, entities, value objects, aggregates, Domain Events and business rules.

*Not yet available* (#6).

### Commands, queries and validation

`PouryaKit.Cqrs`, `PouryaKit.Validation`: commands and queries, their handlers, the fixed pipeline and validators.

*Not yet available* (#7).

### Modules

`PouryaKit.Modularity`: the Composition Root, the Gateway, Operation scopes and the Operation Context.

*Not yet available* (#8, #13).

### Persistence and the Unit of Work

`PouryaKit.Persistence`, `PouryaKit.Persistence.EFCore`, `PouryaKit.Persistence.Postgres`: repositories and specifications, a DbContext and schema per Module, Domain Event Handlers, versions and conflicts.

*Not yet available* (#8, #10, #11, #12).

### Dapper reads

`PouryaKit.Persistence.Dapper`: read queries in plain SQL on a pooled connection.

*Not yet available* (#9).

### Messaging

`PouryaKit.Messaging`, `PouryaKit.Messaging.Wolverine`, `PouryaKit.Messaging.Wolverine.EFCore`: Notification Handlers, Integration Events and Integration Event Handlers, Snapshots and Tombstones.

*Not yet available* (#15, #16, #17, #18).

### Web

`PouryaKit.Web`: endpoints beside their Feature, Result to HTTP, the Operation Context from the caller.

*Not yet available* (#14).

### Testing

`PouryaKit.Testing`: PostgreSQL in a container, data reset between tests, Operations sent through a Gateway, delivery assertions.

*Not yet available* (#8, #15).
