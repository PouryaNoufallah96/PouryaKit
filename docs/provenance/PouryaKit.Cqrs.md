# PouryaKit.Cqrs provenance

Adopted implementations in `PouryaKit.Cqrs`. Format and rules: [README](README.md).

The dispatcher, its fixed pipeline, `Operation` and `IUnitOfWork` are new Kit code (plan section B.2, "Dispatcher + pipeline"; ADR 0002), and so are `OperationContext` and `Actor` (plan section B.2, "Background context"; ADR 0007), so they have no record here. The steps that only read (`Operation.OnlyReading`, ticket #18) and their persistence seam (`IReadingStateGuard`) are new Kit code too. So is request idempotency (`IdempotentAttribute`, `OperationContext.RequestId`, `IIdempotencyStore` and the dispatcher's slot, ticket #42), built to the plan's own rule (I.3, I.4) because both reference implementations its I.3 row cites were rejected there: each writes its marker after the handler on a connection of its own and replays a default success. Nothing was taken from either.

## Package dependencies

Checked against the NuGet cache on 2026-10-06; licence expressions from each package's nuspec. Neither is reciprocal or commercial (ADR 0002), and neither is a mediator.

- `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.12, `MIT`.
- `Microsoft.Extensions.Logging.Abstractions` 10.0.12, `MIT`.

## Commands, queries and their handlers

- **Kit files:** `src/PouryaKit.Cqrs/Command.cs`, `src/PouryaKit.Cqrs/Query.cs`, `src/PouryaKit.Cqrs/CommandHandler.cs`, `src/PouryaKit.Cqrs/QueryHandler.cs`
- **Upstream:** https://github.com/PouryaNoufallah96/cybernod
- **Commit:** bae8ac3b7e46f09e9375647786d03391a500876a
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.Core/Messaging/Commands/Command.cs`, `src/BuildingBlocks/BuildingBlocks.Core/Messaging/Commands/ICommand.cs`, `src/BuildingBlocks/BuildingBlocks.Core/Messaging/Commands/CommandHandler.cs`, `src/BuildingBlocks/BuildingBlocks.Core/Messaging/Commands/ICommandHandler.cs`, `src/BuildingBlocks/BuildingBlocks.Core/Messaging/Queries/Query.cs`, `src/BuildingBlocks/BuildingBlocks.Core/Messaging/Queries/IQuery.cs`, `src/BuildingBlocks/BuildingBlocks.Core/Messaging/Queries/IQueryHandler.cs`
- **Licence:** MIT
- **Adoption:** adapted
- **Ticket:** #7
- **Intentional differences:**
  - Detached from MediatR: no `IRequest` or `IRequestHandler`. The Kit's own dispatcher runs every command and query (ADR 0002).
  - The cache-invalidation keys and tags and the request-header flags of `Command<T>` are dropped (plan section B.2).
  - The `Id` generated for every command and query instance is dropped: it made two equal commands unequal, and who and what caused an Operation belongs to the Operation Context.
  - The abstract records and handler classes are the only way to declare an operation and its handler. The interfaces `ICommand<T>`, `IBaseCommand`, `IQuery<T>`, `IBaseQuery`, `ICommandHandler<,>` and `IQueryHandler<,>` are dropped, so a command or query is always a record.
  - A command that yields no value (`Command`, `CommandHandler<TCommand>`, returning `Result`) is added.

## The order of the Domain Event drain

- **Kit files:** `src/PouryaKit.Cqrs/DomainEventDrain.cs`, `src/PouryaKit.Persistence.EFCore/EfUnitOfWork.cs`
- **Upstream:** https://github.com/kgrzybek/modular-monolith-with-ddd
- **Commit:** 91c8ef24b4cb6ef558c95d8267fa07d68c7059f8
- **Upstream paths:** `src/BuildingBlocks/Infrastructure/DomainEventsDispatching/DomainEventsDispatcher.cs`, `src/BuildingBlocks/Infrastructure/DomainEventsDispatching/DomainEventsDispatcherNotificationHandlerDecorator.cs`, `src/BuildingBlocks/Infrastructure/UnitOfWork.cs`
- **Licence:** MIT
- **Adoption:** pattern
- **Ticket:** #10
- **Intentional differences:**
  - The order is kept: take the Domain Events of every aggregate the DbContext tracks, clear them, run their handlers, then save. Upstream re-runs the dispatcher after each handler through a decorator, with no limit; the Kit repeats in rounds until no aggregate holds a Domain Event and throws after `DomainEventDrain.MaxDepth` rounds.
  - Handlers run one at a time, in the ordinal order of their full type names, instead of through MediatR's publish.
  - A handler returns a `Result`. A failed one, or a nested command that failed during the drain, stops it and rolls the Operation back; upstream handlers signal failure only by throwing.
  - The drain writes nothing to an Outbox: it returns only the Domain Events successfully handled before any failure, and the Unit of Work writes those a Notification Handler subscribes to as Notifications after the prepare step when the Operation succeeds (ticket #15).
