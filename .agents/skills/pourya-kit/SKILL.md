---
name: pourya-kit
description: How to build a .NET modular monolith on PouryaKit. Use when writing, reviewing or testing code in a project that references PouryaKit packages, such as a Module, an aggregate, a command or query, a Domain Event Handler, a Notification Handler, a Dapper read, an Integration Event Handler or an endpoint.
---

# PouryaKit

PouryaKit lets Feature code hold only business behaviour. The Feature supplies the aggregate, the command or query, its validator and handler, its data access and its endpoint. The Kit dispatches the Operation, validates it, owns its single commit, runs its reactions and delivers its Integration Events and Snapshots to other Modules.

These hold in every Capability:

- A handler returns a Result. An expected business failure is an Error in that Result; exceptions are for bugs and infrastructure.
- The Unit of Work writes the Outbox, saves and commits, and sends what it wrote only after the commit. Feature code returns its Result and leaves all of it to the Unit of Work.
- Anything outside a Module reaches it through its Gateway, and every Gateway call is one Operation in its own scope, run with the `OperationContext` the caller passes. There is no anonymous or default Actor.
- A reaction is one of three kinds, kept distinct: a Domain Event Handler inside the Module and its Unit of Work, a Notification Handler after the commit for anything that reaches outside the Module's data, and an Integration Event Handler in the receiving Module.
- Modules talk through Domain Events inside a Module, Integration Events across Modules, and a synchronous question through the owning Module's Contracts only when the answer is needed before the Operation can go on.
- Names follow the Kit's `GLOSSARY.md`: Module, Feature, Operation, Operation Context, Actor, Domain Event, Notification Handler, Integration Event, Snapshot.

## Capabilities

Read the reference file of each Capability the code touches before writing it. A part marked *not yet documented here* has an API this skill does not teach yet: work from the ticket it names, not from the package name.

| Capability | Packages | Read when | Reference |
|---|---|---|---|
| Domain primitives | `PouryaKit.Domain` | Writing an aggregate, entity, value object, typed ID, Domain Event, business rule or Error, or code that needs the time | [domain-primitives.md](references/domain-primitives.md) |
| Commands, queries and validation | `PouryaKit.Cqrs`, `PouryaKit.Validation` | Writing a command, query, handler, validator or authorizer, or sending a nested command | [commands-queries-validation.md](references/commands-queries-validation.md) |
| Modules | `PouryaKit.Modularity` | Writing a Composition Root, composing the Host, calling a Gateway, creating or reading an Operation Context, asking another Module a question, or building an application of one Module | [modules.md](references/modules.md) |
| Persistence and the Unit of Work | `PouryaKit.Persistence`, `.EFCore`, `.Postgres` | Mapping an aggregate, using the repository or a specification, removing an aggregate, opting into audit or soft delete, handling a conflict or a unique value, writing a Domain Event Handler, or adding a migration | [persistence.md](references/persistence.md) |
| Dapper reads | `PouryaKit.Persistence.Dapper` | Writing a query's read in SQL, using typed IDs with Dapper, or writing a Module-internal table with plain SQL in the Unit of Work | [dapper-reads.md](references/dapper-reads.md) |
| Messaging | `PouryaKit.Messaging`, `.Wolverine`, `.Wolverine.EFCore` | Writing a Notification Handler, telling other Modules what happened with an Integration Event, writing an Integration Event Handler, subscribing a second Module to an Integration Event, declaring Notification Handlers, publications and subscriptions in a Composition Root, running deliveries in the Host, renaming or removing a Domain Event, an Integration Event, a handler or a subscription, or giving one a stable name, adding the Receipt table, setting the retries and the inbox stale time together, or finding and replaying dead letters. *Not yet documented here*: Snapshots and Tombstones (#18) | [messaging.md](references/messaging.md) |
| Web | `PouryaKit.Web` | Writing an endpoint, composing the Host's HTTP pipeline, choosing a response's shape, making an endpoint public, reading the caller's claims into the Operation Context, or testing over HTTP | [web.md](references/web.md) |
| Testing | `PouryaKit.Testing` | Writing a test against the hosted application, choosing the Actor a test runs as, fixing the time in a test, waiting for and asserting the deliveries an Operation caused, or reading and replaying its dead letters | [testing.md](references/testing.md) |
