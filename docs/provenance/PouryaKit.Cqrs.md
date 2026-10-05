# PouryaKit.Cqrs provenance

Adopted implementations in `PouryaKit.Cqrs`. Format and rules: [README](README.md).

The dispatcher, its fixed pipeline, `Operation` and `IUnitOfWork` are new Kit code (plan section B.2, "Dispatcher + pipeline"; ADR 0002), so they have no record here.

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
