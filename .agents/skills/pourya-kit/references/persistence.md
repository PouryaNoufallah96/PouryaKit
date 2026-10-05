# Persistence and the Unit of Work

`PouryaKit.Persistence`, `PouryaKit.Persistence.EFCore`, `PouryaKit.Persistence.Postgres`: repositories and specifications, a DbContext and schema per Module, Domain Event Handlers, versions and conflicts. A Module references `PouryaKit.Persistence.Postgres`, which brings the other two.

- [The DbContext and its mappings](#the-dbcontext-and-its-mappings)
- [PostgreSQL](#postgresql)
- [The repository](#the-repository)
- [The Unit of Work](#the-unit-of-work)
- [Domain Event Handlers](#domain-event-handlers)
- [Migrations](#migrations)

A Module's DbContext, a mapping and the aggregate it maps, from the Proving Sample:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PouryaKit.Domain;
using PouryaKit.Persistence.EFCore;

// Infrastructure/Data/Context/
internal sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : PouryaKitDbContext(options);

// Products/Data/Configurations/
internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products");
        builder.Property(product => product.Name).HasMaxLength(200);
    }
}

// Products/Entities/
internal sealed class Product : AggregateRoot<ProductId>
{
    private Product() { }   // for EF Core

    public string Name { get; private set; } = "";

    public CategoryId CategoryId { get; private set; } = null!;   // stored as its value, like the key

    // Create(id, categoryId, name) as in domain-primitives.md: it checks the name and raises ProductCreatedDomainEvent.
}

// In the Composition Root: module.UsePostgres<CatalogDbContext>(connectionName: "app", schema: "catalog");
// In a handler: inject IRepository<Product, ProductId> and call AddAsync, as in commands-queries-validation.md.
```

## The DbContext and its mappings

- One DbContext per Module, `internal sealed`, deriving from `PouryaKitDbContext`, with an empty body. Its conventions store every typed ID declared in its assembly as its value, never map Domain Events, apply every `IEntityTypeConfiguration<T>` in its assembly, and make the Module's schema the default. Override `OnModelCreating` only to add to it, and call the base.
- Write one mapping per aggregate, beside its Business Area, stating only what is specific: table name, lengths, keys the database generates. Write no ID converter and no schema.
- A Guid typed ID created by the application (`TypedId.New<ProductId>()`) needs nothing more. A numeric typed ID the database generates needs `builder.Property(note => note.Id).ValueGeneratedOnAdd();` in its mapping, because conventions never make a converted key database-generated. Its `Id` stays null until the Unit of Work saves, after the handler has returned.

## PostgreSQL

- `UsePostgres<TDbContext>(connectionName, schema)` on the Module's builder reads the connection string `ConnectionStrings:{connectionName}` from the Host's configuration, gives the Module its own schema with its own `__EFMigrationsHistory`, names tables and columns in snake_case, and makes the DbContext the Module's Unit of Work. Modules may share one connection string; each keeps its own schema.

## The repository

- Inject `IRepository<TAggregate, TId>`: `AddAsync`, `Remove`, `FindAsync(id)`, and `FindAsync`, `ListAsync`, `AnyAsync`, `CountAsync` by an Ardalis.Specification `Specification<T>`. It tracks changes in the Operation's Unit of Work; nothing on it saves. Feature code reaches its data through it, never through the DbContext.

A named specification and a Feature that uses it, from the Proving Sample's Products Business Area:

```csharp
using Ardalis.Specification;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

// Products/Data/Specifications/
internal sealed class ProductsNamed : Specification<Product>
{
    public ProductsNamed(IReadOnlyCollection<string> names) => Query.Where(product => names.Contains(product.Name));
}

// Products/Features/RemoveProducts/: the generic repository and the specification are enough
internal sealed class RemoveProductsCommandHandler(IRepository<Product, ProductId> products)
    : CommandHandler<RemoveProductsCommand, int>
{
    public override async Task<Result<int>> Handle(RemoveProductsCommand command, CancellationToken ct)
    {
        var named = await products.ListAsync(new ProductsNamed(command.Names), ct);
        foreach (var product in named) products.Remove(product);
        return named.Count;
    }
}
```

- Name a specification for what it selects and put it beside its Business Area in `Data/Specifications/`: an `internal sealed` class deriving from `Specification<T>` that builds its query in its constructor. Any Feature of the Module may use it with the repository's specification members. `FindAsync` returns the first aggregate it selects, or null. `AnyAsync` and `CountAsync` apply only its filters, never its ordering, paging or includes.
- An aggregate the repository returns is tracked, unless its specification turns tracking off: change it through its methods and return; the Unit of Work saves the change.

The Proving Sample's aggregate-specific repository. Its import needs only the product names already in use, a projection the generic members, which answer with aggregates, a flag or a count, cannot give:

```csharp
using Ardalis.Specification;
using PouryaKit.Cqrs;
using PouryaKit.Persistence;
using PouryaKit.Persistence.EFCore;

// Products/Data/Specifications/: a projection specification selects a value, not the aggregate
internal sealed class ProductNamesInUse : Specification<Product, string>
{
    public ProductNamesInUse(IReadOnlyCollection<string> names)
    {
        Query.Where(product => names.Contains(product.Name));
        Query.Select(product => product.Name);
    }
}

// Products/Data/Repository/
internal interface IProductRepository : IRepository<Product, ProductId>
{
    Task<List<string>> ListNamesInUseAsync(IReadOnlyCollection<string> names, CancellationToken ct = default);
}

internal sealed class ProductRepository(IUnitOfWork unitOfWork)
    : EfRepository<Product, ProductId>(unitOfWork), IProductRepository
{
    public async Task<List<string>> ListNamesInUseAsync(IReadOnlyCollection<string> names, CancellationToken ct = default) =>
        [.. (await ListAsync(new ProductNamesInUse(names), ct)).Distinct()];
}

// In the Composition Root: module.AddScoped<IProductRepository, ProductRepository>();
// In ImportProductsCommandHandler(IProductRepository products):
//     var inUse = await products.ListNamesInUseAsync(names, ct);  ...  await products.AddAsync(product.Value, ct);
```

- Add an aggregate-specific repository only when a Business Area needs a query the generic contract cannot express, such as the projection above. A filter is a specification, not a repository member, and a wrapper that only forwards the generic members is not warranted. Features that need nothing more keep injecting `IRepository<,>`.
- Declare it as an `internal` interface deriving from `IRepository<TAggregate, TId>` with only the extra members, in `Data/Repository/` beside its Business Area. Implement it with an `internal sealed` class deriving from `EfRepository<TAggregate, TId>` (`PouryaKit.Persistence.EFCore`), taking `IUnitOfWork` and passing it to the base: it inherits every generic member.
- Build each extra member on the inherited members, or on a projection specification (`Specification<TAggregate, TResult>` with `Query.Select`) passed to the protected `ListAsync<TResult>`. It applies the whole specification, its ordering and paging included, and returns a loaded list; finish the result in memory, as `Distinct` does above.
- Register it in the Module's Composition Root: `module.AddScoped<IProductRepository, ProductRepository>()`. It tracks in the same Unit of Work as the generic repository, so a Feature may use both.
- The base class gives it no query, no DbSet and no DbContext, so it has no way to save and no way to write around the Unit of Work. Never add one: the Unit of Work saves what the repositories track.

## The Unit of Work

- Each command Operation runs in one transaction that the Unit of Work owns. When the outermost handler returns a success and no nested command failed, it runs the Domain Event Handlers, checks again that nothing failed, then saves once and commits. A failed Result, a failed nested command or a failed Domain Event Handler rolls everything back; an exception rolls back and reaches the Gateway's caller.
- Nothing retries: no retrying execution strategy, no retry in the Unit of Work. To retry, send the command again through the Gateway.
- Today the Unit of Work leaves `Version` at 0: versions arrive with #11.

## Domain Event Handlers

A Domain Event Handler is a reaction inside the Module that commits with the change that caused it, or not at all. The Proving Sample's Categories Business Area counts each new product in its category:

```csharp
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

// Categories/Events/DomainEventHandlers/
internal sealed class CountProductInCategoryHandler(IRepository<Category, CategoryId> categories)
    : IDomainEventHandler<ProductCreatedDomainEvent>
{
    public async Task<Result> Handle(ProductCreatedDomainEvent domainEvent, CancellationToken ct)
    {
        var category = await categories.FindAsync(domainEvent.CategoryId, ct);
        if (category is null) return CategoryErrors.NotFound;   // the product is not created either

        category.CountProduct();
        return Result.Success();                                 // no save: the Unit of Work saves both
    }
}
```

- Implement `IDomainEventHandler<TDomainEvent>` (`PouryaKit.Cqrs`) for one Domain Event type, in an `internal sealed` class in the Business Area that reacts, under `Events/DomainEventHandlers/`. `AddPouryaKitCqrs` registers it with the Module's other handlers. A class that handles several Domain Event types, or an abstract one, fails at startup.
- It runs only for a Domain Event of exactly its type, never for one derived from it. A Domain Event may have several handlers or none; one with no handler is fine, and the Operation commits.
- It runs after the outermost command's handler returned a success and no nested command failed, before the save, in the same transaction. Handlers run one at a time, never concurrently; the handlers of one Domain Event run in the ordinal order of their full type names. An aggregate's Domain Events are handled in the order it raised them.
- The Unit of Work takes the Domain Events of every aggregate it tracks: those added, found or listed through a repository in the Operation. It clears them as it takes them.
- A handler may change other aggregates through repositories, and they may raise Domain Events of their own: those are handled in the next round, until none remain, for at most `DomainEventDrain.MaxDepth` rounds. Domain Events still raised after that throw `InvalidOperationException` and nothing commits: handlers that keep raising each other's Domain Events are a cycle, which is a bug.
- A failed Result stops the handling at once, so no further handler or Domain Event runs; the whole Operation rolls back and fails with that handler's Errors. An exception rolls back and reaches the Gateway's caller.
- A handler may send a nested command with an injected `IDispatcher`; it joins the same Unit of Work. If it fails, the whole Operation rolls back even when the handler ignores its Result and returns a success, and the Operation fails with the nested command's Errors.
- A handler returns a Result and never saves, commits, begins a transaction or touches the Outbox.
- Choose a Domain Event Handler to keep the Module's own aggregates consistent with each other in the same commit. Anything that reaches outside the Module's own data, such as sending an email, pushing to a client or publishing an Integration Event, belongs in a Notification Handler, which runs after the commit (#15, not yet available): a Domain Event Handler runs before the commit, so whatever it sent would stay sent when the Operation rolls back.
- Test it through the Gateway with the command whose aggregate raises its Domain Event: assert that both aggregates' rows are written, and that a failed reaction leaves neither.

## Migrations

- Each Module keeps its migrations in its own folder. The Host project references `Microsoft.EntityFrameworkCore.Design` with `PrivateAssets="all"`, and the solution's tool manifest holds `dotnet-ef`. From the folder that holds `src/` (in the Proving Sample, `samples/ModularMonolith`):

```bash
dotnet ef migrations add Initial \
  --project src/Modules/Catalog/Catalog \
  --startup-project src/ModularMonolith.Host \
  --context CatalogDbContext \
  --output-dir Infrastructure/Data/Migrations
```

- `dotnet-ef` builds the Host as its `Program` does and finds the internal DbContext in its container, so the DbContext stays internal and needs no design-time factory. Name it with `--context`; set the folder with `--output-dir` and the namespace follows.
- The Host applies no migration at startup. `HostFixture` applies every Module's in tests.

*Not yet available*: audit, soft delete, versions and conflicts (#11).
