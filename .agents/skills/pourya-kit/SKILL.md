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

Read the reference file of each Capability the code touches before writing it. A Capability marked *not yet available* has no API yet: work from the ticket it names, not from the package name.

| Capability | Packages | Read when | Reference |
|---|---|---|---|
| Domain primitives | `PouryaKit.Domain` | Writing an aggregate, entity, value object, typed ID, Domain Event, business rule or Error, or code that needs the time | [domain-primitives.md](references/domain-primitives.md) |
| Commands, queries and validation | `PouryaKit.Cqrs`, `PouryaKit.Validation` | Writing a command, query, handler, validator or authorizer, or sending a nested command | [commands-queries-validation.md](references/commands-queries-validation.md) |
| Modules | `PouryaKit.Modularity` | Writing a Composition Root, composing the Host, or calling a Gateway | [modules.md](references/modules.md) |
| Persistence and the Unit of Work | `PouryaKit.Persistence`, `.EFCore`, `.Postgres` | Mapping an aggregate, using the repository or a specification, writing a Domain Event Handler, or adding a migration | [persistence.md](references/persistence.md) |
| Dapper reads | `PouryaKit.Persistence.Dapper` | *Not yet available* (#9) | |
| Messaging | `PouryaKit.Messaging`, `.Wolverine`, `.Wolverine.EFCore` | *Not yet available* (#15, #16, #17, #18): Notification Handlers, Integration Events and their handlers, Snapshots and Tombstones | |
| Web | `PouryaKit.Web` | *Not yet available* (#14): endpoints beside their Feature, Result to HTTP, the Operation Context from the caller | |
| Testing | `PouryaKit.Testing` | Writing a test against the hosted application, or fixing the time in a test | [testing.md](references/testing.md) |
