# Dapper reads

`PouryaKit.Persistence.Dapper`: queries in plain SQL with Dapper on a pooled PostgreSQL connection, and typed IDs as Dapper parameters and results. Namespace `PouryaKit.Persistence.Dapper`. A plain-SQL write that joins the Unit of Work uses `IUnitOfWorkConnection` from `PouryaKit.Persistence` instead.

- [Registration](#registration)
- [Reading](#reading)
- [Typed IDs](#typed-ids)
- [Writing with plain SQL in the Unit of Work](#writing-with-plain-sql-in-the-unit-of-work)

The Proving Sample's `GetProduct` and `ListProducts` Features:

```csharp
using Dapper;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence.Dapper;

// Products/Features/GetProduct/
internal sealed record ProductDto(Guid Id, Guid CategoryId, string Name);

internal sealed class GetProductQueryHandler(IReadConnectionFactory connections) : QueryHandler<GetProductQuery, ProductDto>
{
    public override async Task<Result<ProductDto>> Handle(GetProductQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var product = await connection.QuerySingleOrDefaultAsync<ProductDto>(
            new CommandDefinition(
                "select id, category_id as categoryid, name from catalog.products where id = @Id and is_deleted = false",
                new { Id = new ProductId(query.ProductId) },   // a typed ID as a parameter
                cancellationToken: cancellationToken
            )
        );
        return product is null ? ProductErrors.NotFound : product;
    }
}

// Products/Features/ListProducts/: typed IDs as results
internal sealed record ProductListItemDto(ProductId Id, CategoryId CategoryId, string Name);

internal sealed class ListProductsQueryHandler(IReadConnectionFactory connections)
    : QueryHandler<ListProductsQuery, List<ProductListItemDto>>
{
    public override async Task<Result<List<ProductListItemDto>>> Handle(ListProductsQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var products = await connection.QueryAsync<ProductListItemDto>(
            new CommandDefinition(
                "select id, category_id as categoryid, name from catalog.products where is_deleted = false order by name, id",
                cancellationToken: cancellationToken
            )
        );
        return products.AsList();
    }
}
```

## Registration

- Call `module.AddPouryaKitDapper(connectionName: "app", typeof(CatalogModule).Assembly)` in the Composition Root of each Module that reads with Dapper. It reads the connection string `ConnectionStrings:{connectionName}` from the Host's configuration, and makes every typed ID in the assemblies given usable with Dapper.
- The Host holds one `IReadConnectionFactory` and one connection pool, shared by every Module that reads with Dapper. Every Module names the same connection; naming a different one in the same Host throws at startup. A missing connection string throws when `IReadConnectionFactory` is first resolved, not when the Host is built.

## Reading

- Read in a query's handler. Inject `IReadConnectionFactory` and open a connection with `await using var connection = await connections.OpenAsync(cancellationToken);`. Each call leases a new open connection from the pool; the handler owns it and disposes it, which returns it to the pool. Opening with a cancelled token throws `OperationCanceledException`.
- A read connection never joins the Operation's Unit of Work. It reads what is committed: inside a command it does not see the Operation's own changes, which the Unit of Work saves only after the handlers return.
- Pass the cancellation token through `new CommandDefinition(sql, parameters, cancellationToken: cancellationToken)`; Dapper's overloads that take the SQL and parameters directly take no token.
- Name each table with its Module's schema (`catalog.products`): the read connection is the Host's, not the Module's. Read only your own Module's schema; another Module's data comes through its Contracts (see [modules.md](modules.md)).
- Columns are in snake_case (see [persistence.md](persistence.md)); alias each to its member name (`category_id as categoryid`). Dapper matches names without regard to case.
- Dapper runs exactly the SQL you write. No EF Core query filter applies to it, so state every predicate the read needs in the SQL itself, as the Proving Sample's reads state `is_deleted = false` because EF Core's soft-delete filter does not reach them. Soft delete itself is not yet documented here (#11).
- Read into an `internal` DTO record declared in the Feature, as above, and return a missing row as the Business Area's `NotFound` Error.

## Typed IDs

- A typed ID declared in an assembly passed to `AddPouryaKitDapper` works as a parameter and as a result with no code of its own: `new { Id = new ProductId(id) }` sends its value, and a DTO member typed `ProductId` reads its column back into one. Guid, long, int and string IDs all work; a column of another numeric type, such as an integer column read into a long ID, is converted.
- Pass every assembly whose typed IDs you read or write with Dapper; usually that is the Module's own.

## Writing with plain SQL in the Unit of Work

A Module-internal table that no aggregate maps, such as a projection, a read model or a tally, may be written with plain SQL that commits or rolls back with the Operation. Inject `IUnitOfWorkConnection` (`PouryaKit.Persistence`, registered by `UsePostgres`) and pass its transaction to every statement:

```csharp
using Dapper;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

// Notes/Features/WriteNote/: the note through its repository, its tally in a projection table
internal sealed class WriteNoteCommandHandler(IRepository<Note, NoteId> notes, IUnitOfWorkConnection unitOfWork)
    : CommandHandler<WriteNoteCommand>
{
    public override async Task<Result> Handle(WriteNoteCommand command, CancellationToken cancellationToken)
    {
        await notes.AddAsync(Note.Write(command.Text), cancellationToken);

        var (connection, transaction) = unitOfWork.Join();
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                insert into notes.note_tallies (text, count) values (@Text, 1)
                on conflict (text) do update set count = note_tallies.count + 1
                """,
                new { command.Text },
                transaction,
                cancellationToken: cancellationToken
            )
        );
        return Result.Success();
    }
}
```

- `Join()` returns the active Unit of Work's connection and transaction. Call it only from a command's handler or a Domain Event Handler. It throws `InvalidOperationException` in a query, an authorizer or a validator at any depth, including a query sent from a command's handler, and wherever no Unit of Work is active.
- Pass the transaction to every statement through `CommandDefinition`. The connection and the transaction belong to the Unit of Work: never dispose, commit or roll back either.
- Write only to the Module's own tables that no aggregate maps. An aggregate changes only through its repository; a plain-SQL write to its table bypasses its version and its Snapshots.
- The statement runs when you call it, inside the transaction, before the Unit of Work saves what the repositories track. It cannot see or reference rows added through a repository in the same Operation.
- If the Operation fails anywhere, the write rolls back with everything else; on a success it commits in the same commit.
