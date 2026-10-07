# Persistence and the Unit of Work

`PouryaKit.Persistence`, `PouryaKit.Persistence.EFCore`, `PouryaKit.Persistence.Postgres`: repositories and specifications, a DbContext and schema per Module, the Unit of Work and its Domain Event Handlers, audit and soft delete, versions and conflicts, unique values. A Module references `PouryaKit.Persistence.Postgres`, which brings the other two. Reads for display go through Dapper instead ([dapper-reads.md](dapper-reads.md)).

- [The DbContext and its mappings](#the-dbcontext-and-its-mappings)
- [PostgreSQL](#postgresql)
- [The repository](#the-repository)
- [The Unit of Work](#the-unit-of-work)
- [Audit and soft delete](#audit-and-soft-delete)
- [Versions and conflicts](#versions-and-conflicts)
- [Unique values](#unique-values)
- [Domain Event Handlers](#domain-event-handlers)
- [Migrations](#migrations)
- [Idempotency key retention](#idempotency-key-retention)

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
    public const string NameIndex = "ix_products_name";   // the Composition Root maps its violation

    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products");
        builder.Property(product => product.Name).HasMaxLength(200);
        // Unique among the products not removed, so a removed product's name is free again.
        builder.HasIndex(product => product.Name).IsUnique().HasFilter("is_deleted = false").HasDatabaseName(NameIndex);
    }
}

// Products/Entities/: auditable and soft-deletable, so the Unit of Work sets those properties and the domain never does
internal sealed class Product : AggregateRoot<ProductId>, IAuditable, ISoftDeletable
{
    // Set by Remove, not mapped: IsDeleted is set only by the Unit of Work, after every handler of the Operation ran.
    private bool _removed;

    private Product() { }   // for EF Core

    public string Name { get; private set; } = "";

    public CategoryId CategoryId { get; private set; } = null!;   // stored as its value, like the key

    public DateTimeOffset CreatedAt { get; private set; }

    public string CreatedBy { get; private set; } = "";

    public DateTimeOffset LastChangedAt { get; private set; }

    public string LastChangedBy { get; private set; } = "";

    public bool IsDeleted { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public string? DeletedBy { get; private set; }

    // Create(id, categoryId, name) as in domain-primitives.md: it checks the name and raises ProductCreatedDomainEvent.

    /// <summary>
    /// Records that the product is leaving the catalog, once; the repository's Remove then removes it. Removing it
    /// again in the same Operation raises nothing: it is still found until the Unit of Work marks it deleted.
    /// </summary>
    public void Remove()
    {
        if (_removed) return;
        _removed = true;
        Raise(new ProductRemovedDomainEvent(Id, CategoryId));
    }
}

// In the Composition Root:
//     module.UsePostgres<CatalogDbContext>(connectionName: "app", schema: "catalog");
//     module.MapUniqueConstraint<CatalogDbContext>(ProductConfiguration.NameIndex, ProductErrors.NameInUse);
// In a handler: inject IRepository<Product, ProductId> and call AddAsync, as in commands-queries-validation.md.
```

## The DbContext and its mappings

- One DbContext per Module, `internal sealed`, deriving from `PouryaKitDbContext`, with an empty body. Its conventions store every typed ID declared in its assembly as its value, never map Domain Events, apply every `IEntityTypeConfiguration<T>` in its assembly, and make the Module's schema the default. They also make every aggregate's `Version` its concurrency token and leave every removed soft-deletable entity out of queries. Override `OnModelCreating` only to add to it, and call the base.
- Write one mapping per aggregate, beside its Business Area, stating only what is specific: table name, lengths, unique indexes, owned types and children, keys the database generates. Write no ID converter, no schema, no concurrency token, no query filter and nothing for the audit or soft-delete properties.
- A Guid typed ID created by the application (`TypedId.New<ProductId>()`) needs nothing more. A numeric typed ID the database generates needs `builder.Property(note => note.Id).ValueGeneratedOnAdd();` in its mapping, because conventions never make a converted key database-generated. Its `Id` stays null until the Unit of Work saves, after the handler has returned, so such an aggregate cannot publish Snapshots ([messaging.md](messaging.md#the-rules-of-a-mapper-and-a-key)).
- What an aggregate owns is an owned type (`OwnsOne`, `OwnsMany`) or a child entity it holds in a navigation, mapped with a foreign key from that navigation: `builder.HasMany(folder => folder.Pages).WithOne().IsRequired();`. That key is how the Unit of Work finds the aggregate a changed child belongs to. A child the aggregate reaches by no navigation is not part of it, and neither is another aggregate referenced by its typed ID, as `Product.CategoryId` is.
- Change a part of an aggregate only through the aggregate, loaded through its repository in the same Operation. A changed part whose aggregate the Operation did not load throws `InvalidOperationException` when the Operation commits, and nothing is committed: an aggregate that was not loaded cannot advance its version, so a concurrent change would not conflict.

## PostgreSQL

- `UsePostgres<TDbContext>(connectionName, schema)` on the Module's builder reads the connection string `ConnectionStrings:{connectionName}` from the Host's configuration, gives the Module its own schema with its own `__EFMigrationsHistory`, names tables and columns in snake_case, and makes the DbContext the Module's Unit of Work. Modules may share one connection string; each keeps its own schema. A connection string the Host's final configuration does not set, or sets blank, fails the Host's start among the composition checks, one line per connection naming its DbContexts and its key, `ConnectionStrings:{connectionName}`; nothing connects ([modules.md](modules.md#startup-checks)).
- It also lets the Unit of Work read which unique index a refused save violated, for the Errors a Module maps with `MapUniqueConstraint` ([Unique values](#unique-values)).

## The repository

- Inject `IRepository<TAggregate, TId>`: `AddAsync`, `Remove`, `FindAsync(id)`, and `FindAsync`, `ListAsync`, `AnyAsync`, `CountAsync` by an Ardalis.Specification `Specification<T>`. It tracks changes in the Operation's Unit of Work; nothing on it saves. Feature code reaches its data through it, never through the DbContext: a structure test rejects a Module that calls `SaveChanges`, `DbContext.Database` or EF Core's bulk `ExecuteUpdate` and `ExecuteDelete` ([testing.md](testing.md#structure-tests)).
- Add and remove only where the Operation may change state. `AddAsync` and `Remove` throw `InvalidOperationException` in [code that only reads](commands-queries-validation.md#code-that-only-reads), because a query nested in a command would otherwise write into the command's commit.

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
        foreach (var product in named)
        {
            product.Remove();           // raises ProductRemovedDomainEvent, which its category reacts to
            products.Remove(product);   // the Unit of Work keeps the row, marked deleted
        }

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
// In ImportProductsCommandHandler(IProductRepository products): a name given twice is refused first, with
// ProductErrors.NameRepeated (a validation Error); then
//     var inUse = await products.ListNamesInUseAsync(names, ct);  ...  await products.AddAsync(product.Value, ct);
// Skipping the names in use is a courtesy, not the guard: the unique index is, so a name a concurrent Operation takes
// first fails the import with ProductErrors.NameInUse.
```

- Add an aggregate-specific repository only when a Business Area needs a query the generic contract cannot express, such as the projection above. A filter is a specification, not a repository member, and a wrapper that only forwards the generic members is not warranted. Features that need nothing more keep injecting `IRepository<,>`.
- Declare it as an `internal` interface deriving from `IRepository<TAggregate, TId>` with only the extra members, in `Data/Repository/` beside its Business Area. Implement it with an `internal sealed` class deriving from `EfRepository<TAggregate, TId>` (`PouryaKit.Persistence.EFCore`), taking `IUnitOfWork` and passing it to the base: it inherits every generic member.
- Build each extra member on the inherited members, or on a projection specification (`Specification<TAggregate, TResult>` with `Query.Select`) passed to the protected `ListAsync<TResult>`. It applies the whole specification, its ordering and paging included, and returns a loaded list; finish the result in memory, as `Distinct` does above.
- Register it in the Module's Composition Root: `module.AddScoped<IProductRepository, ProductRepository>()`. It tracks in the same Unit of Work as the generic repository, so a Feature may use both.
- The base class gives it no query, no DbSet and no DbContext, so it has no way to save and no way to write around the Unit of Work. Never add one: the Unit of Work saves what the repositories track.

### Reading a Page

A read normally goes through Dapper ([dapper-reads.md](dapper-reads.md#reading-a-page)). When a Feature reads its Page through EF Core instead, a specific repository builds the member on the protected `PageAsync<TResult>`, with `PageExpressions<TAggregate>` mapping each name its query's `PageDeclaration` declares ([commands-queries-validation.md](commands-queries-validation.md#paging)). The Proving Sample reads `ListProducts`'s Page this way in its tests, to prove both helpers answer the same Page:

```csharp
using Ardalis.Specification;
using PouryaKit.Cqrs;
using PouryaKit.Persistence.EFCore;

internal sealed class ProductPages(IUnitOfWork unitOfWork) : EfRepository<Product, ProductId>(unitOfWork)
{
    private static readonly PageExpressions<Product> Paging = new PageExpressions<Product>(ListProductsQuery.Paging)
        .OrderBy("name", product => product.Name)
        .OrderBy("created", product => product.CreatedAt)
        .ThenBy(product => product.Id)                                   // unique: equal names keep one order
        .Filter<string>("name", FilterOperator.Equal, name => product => product.Name == name)
        .Filter<string>("name", FilterOperator.Contains, name => product => product.Name.Contains(name))
        .Filter<Guid>("category", FilterOperator.Equal, id => product => product.CategoryId == new CategoryId(id))
        .Filter<DateTimeOffset>("created", FilterOperator.GreaterThanOrEqual, since => product => product.CreatedAt >= since);

    public Task<Page<ProductListItemDto>> PageAsync(PageRequest request, CancellationToken ct) =>
        PageAsync(new ListItems(), Paging, request, ct);

    private sealed class ListItems : Specification<Product, ProductListItemDto>
    {
        public ListItems() => Query.Select(product => new ProductListItemDto(product.Id, product.CategoryId, product.Name));
    }
}
```

- Map every declared sort key with `OrderBy`, every declared field and operator with `Filter`, whose value type must be the field's, and name a unique `ThenBy` key. Each call returns a copy; keep the result in a static field. A request whose sort key or filter is undeclared, or expressions that miss part of the declaration, throw `InvalidOperationException` before any query runs.
- `PageAsync` applies the specification's criteria and the request's filters, counts that selection, orders it, skips and takes the Page and projects it with the specification's `Select`. Give the specification criteria and a `Select` only: one with ordering, or that sets `Skip` or `Take` at all, `Take(0)` included, throws. The global query filters apply, so removed soft-deletable rows stay out.
- A filter value reaches the database as a parameter of your predicate. `Contains` on PostgreSQL is case-sensitive, as `strpos` is in SQL.

## The Unit of Work

- Each command Operation runs in one transaction that the Unit of Work begins and owns, and commits in a fixed sequence:
  1. The outermost handler returns. A failed Result, or a nested command that failed, ends the Operation here.
  2. The Domain Event Handlers run ([below](#domain-event-handlers)), and the Unit of Work checks again that nothing failed.
  3. It prepares persistence: soft-delete marks, the cascades of what was deleted, one version advance per changed aggregate, audit fields ([below](#audit-and-soft-delete)).
  4. It builds the Snapshot, or the Tombstone, of each changed aggregate that a Snapshot is declared for, from that final state, carrying the version the prepare step set ([messaging.md](messaging.md#snapshots-between-modules)).
  5. It writes the Operation's Notifications, the Integration Events a Notification Handler's delivery published, and the Snapshots to the Outbox, checks once more that nothing failed, because a Snapshot mapper may have caught a refused command, saves once and commits, and only after the commit sends them ([messaging.md](messaging.md#what-the-kit-guarantees)).

  Nothing changes an aggregate after the prepare step: a Snapshot mapper that does fails the Operation.
- A failed Result, a failed nested command or a failed Domain Event Handler rolls everything back, and nothing is prepared or written to the Outbox. An exception rolls back and reaches the Gateway's caller.
- Code that only reads cannot change tracked state, including through a held aggregate reference. The EF Unit of Work supplies the Operation's `IReadingStateGuard`, which compares the state across queries, authorization and validation, and `OnlyReading` steps. It preserves legitimate pending command changes as the baseline and permits new tracked reads. Capture and comparison detect EF relationship changes even when automatic change detection is disabled, without changing that setting. A difference marks the Operation rollback-only before throwing, so a swallowed exception still prevents a commit. The shared `TrackedState` comparison includes mutable mapped values, child and owned entities, complex collections, and pending Domain Event identity and order. State restored before comparison is not detected ([code that only reads](commands-queries-validation.md#code-that-only-reads)). These are Kit seams; Feature code never calls them.
- Steps 4 and 5, from the Snapshots to the send, are a Kit seam, `IUnitOfWorkOutbox` (`PouryaKit.Persistence.EFCore`), which `UsePouryaKitWolverine(connectionName)` registers. Feature code never uses it.
- A save the database refuses for a concurrent change, or for a unique index the Module mapped, rolls everything back and fails the Operation with an Error ([Versions and conflicts](#versions-and-conflicts), [Unique values](#unique-values)). Any other database error stays an exception.
- Nothing retries: no retrying execution strategy, no retry in the Unit of Work. To retry, send the command again through the Gateway.
- A plain-SQL write to a Module-internal table that no aggregate maps joins this transaction through `IUnitOfWorkConnection` (`PouryaKit.Persistence`), from a command's handler, a Domain Event Handler, or a Notification or Integration Event Handler only; see [dapper-reads.md](dapper-reads.md).

## Audit and soft delete

Both are opt-in, per aggregate or entity, with a contract from `PouryaKit.Domain`, as the `Product` above shows:

- `IAuditable`: `CreatedAt`, `CreatedBy`, `LastChangedAt`, `LastChangedBy`. `ISoftDeletable`: `IsDeleted`, `DeletedAt`, `DeletedBy`. Declare each as a property with a private setter and never set it: the Unit of Work does. They map by convention to snake_case columns; add a migration.
- The Unit of Work sets them in its prepare step, once per Operation, after the Domain Event Handlers and before the save. Every stamp of an Operation has one time, from the Operation's clock (the Host's `TimeProvider`, the same clock its Domain Events read), and one Actor, the `Actor.Id` of its Operation Context.
- An auditable change, or the removal of a soft-deletable entity, in a scope whose Operation was never begun throws `InvalidOperationException`, and nothing commits: the stamp needs an Actor. An Operation that stamps nothing needs no context.
- Audit: an added entity gets its creation and last change from the Operation; a changed one gets its last change. A change to anything an aggregate owns counts as a change of its root, so an auditable root is stamped then too, and a child moved from one aggregate to another stamps both.
- Soft delete: `Remove` on the repository of a soft-deletable aggregate keeps its row. The prepare step turns the removal into `IsDeleted = true` with `DeletedAt` and `DeletedBy`, and everything the aggregate owns is kept with it. Removing an aggregate that is not soft-deletable deletes its row.
- EF Core queries leave a removed entity out by convention, so the repository never returns one in a later Operation. A Dapper read must say `is_deleted = false` itself ([dapper-reads.md](dapper-reads.md)).
- Removal raises no Domain Event of its own; an aggregate that publishes Snapshots publishes its Tombstone ([messaging.md](messaging.md#snapshots-between-modules)). When other aggregates care, give the aggregate a domain method that raises one and call it before the repository's `Remove`, as `RemoveProducts` above calls `Product.Remove()`. Guard the method so that a second call in the same Operation raises nothing more, as `Product` does.
- Put `ISoftDeletable` on a type EF Core can filter: an aggregate or an entity that is not mapped as an owned type, at the root of its inheritance hierarchy or below a root that implements it too. An owned type, or a type derived from a root that is not soft-deletable, is refused when the model is built.
- A soft-deletable child is kept when it is removed from its aggregate. It is deleted outright only when its parent is deleted outright and the child's key to that parent is required: its row cannot outlive the parent's. The Unit of Work resolves every such cascade in the prepare step, so the save itself changes nothing further.

## Versions and conflicts

- Every aggregate's `Version` (`AggregateRoot<TId>`) is its concurrency token by convention. It is 0 until saved, 1 after its first commit, and exactly one more for each later Operation that changes the aggregate or anything it owns, however many times that Operation changed it. A removal counts, and so does a part changed, added or removed. A child moved from one aggregate to another changes both, and a child detached from its parent changes the parent it left; each must be loaded in the Operation ([mappings](#the-dbcontext-and-its-mappings)). An Operation that changes nothing leaves it as it was, and raising a Domain Event alone does not move it.
- Feature code never sets `Version` and adds no other concurrency token or row lock: the version is the one concurrency mechanism.
- The save checks that each changed aggregate still has the version the Operation read. When another Operation committed a change to it first, the Unit of Work rolls everything back and the Operation fails with `UnitOfWorkErrors.ConcurrencyConflict` (`PouryaKit.Persistence.EFCore`, kind `Conflict`, code `UnitOfWork.ConcurrencyConflict`). That holds for a change with or without Domain Events.
- Nothing retries the Operation. Its caller decides: send the command again through the Gateway, which runs a new Operation on the current state, or report the conflict, which answers 409 over HTTP ([web.md](web.md)).

## Unique values

- Guard a unique value with a unique index in the mapping, never with a check before the write: a check reads only what is committed and cannot see a concurrent Operation. Name the index with a constant (`HasDatabaseName(NameIndex)`, as above). On a soft-deletable table, filter the index to the rows not removed (`HasFilter("is_deleted = false")`, SQL with snake_case names), so a removed aggregate's value is free again.
- Map its violation to the Module's own Error in the Composition Root: `module.MapUniqueConstraint<CatalogDbContext>(ProductConfiguration.NameIndex, ProductErrors.NameInUse)` (`PouryaKit.Persistence.EFCore`). The Error is usually of kind `Conflict`. Map each index once; mapping one twice for the same DbContext throws at composition.
- The mapping covers a unique index or constraint the database checks when the row is written. A constraint declared `DEFERRABLE INITIALLY DEFERRED` is checked at commit, and its violation is an exception, not the mapped Error.
- When the save violates a mapped index, the Unit of Work rolls everything back and the Operation fails with that Error. Any other database error, such as an unmapped constraint, a duplicate primary key or a foreign key, stays an exception: it rolls back and reaches the Gateway's caller.
- A check before the write may still make the common case friendlier, as the import above skips names already in use; the index stays the guard.

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

Its counterpart, `UncountProductInCategoryHandler`, handles `ProductRemovedDomainEvent` the same way and lowers the count, so a removal and its category's count commit together.

- Implement `IDomainEventHandler<TDomainEvent>` (`PouryaKit.Cqrs`) for one Domain Event type, in an `internal sealed` class in the Business Area that reacts, under `Events/DomainEventHandlers/`. `AddPouryaKitCqrs` registers it with the Module's other handlers. A class that handles several Domain Event types, or an abstract one, fails at startup.
- It runs only for a Domain Event of exactly its type, never for one derived from it. A Domain Event may have several handlers or none; one with no handler is fine, and the Operation commits.
- It runs after the outermost command's handler returned a success and no nested command failed, before the prepare step and the save, in the same transaction. Handlers run one at a time, never concurrently; the handlers of one Domain Event run in the ordinal order of their full type names. An aggregate's Domain Events are handled in the order it raised them.
- The Unit of Work takes the Domain Events of every aggregate it tracks: those added, found, listed or removed through a repository in the Operation. It clears them as it takes them.
- A handler may change other aggregates through repositories, and they may raise Domain Events of their own: those are handled in the next round, until none remain, for at most `DomainEventDrain.MaxDepth` rounds. Domain Events still raised after that throw `InvalidOperationException` and nothing commits: handlers that keep raising each other's Domain Events are a cycle, which is a bug.
- A failed Result stops the handling at once, so no further handler or Domain Event runs; the whole Operation rolls back and fails with that handler's Errors. An exception rolls back and reaches the Gateway's caller.
- A handler may send a nested command with an injected `IDispatcher`; it joins the same Unit of Work. If it fails, the whole Operation rolls back even when the handler ignores its Result and returns a success, and the Operation fails with the nested command's Errors.
- A handler returns a Result and never saves, commits, begins a transaction or touches the Outbox ([commands-queries-validation.md](commands-queries-validation.md#commands-and-queries)).
- Choose a Domain Event Handler to keep the Module's own aggregates consistent with each other in the same commit. Anything that reaches outside the Module's own data, such as sending an email, pushing to a client or publishing an Integration Event, belongs in a Notification Handler, which runs after the commit ([messaging.md](messaging.md)): a Domain Event Handler runs before the commit, so whatever it sent would stay sent when the Operation rolls back.
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
- Opting an aggregate into audit or soft delete, or adding a unique index, changes the model: add a migration for it.
- A Module with an `[Idempotent]` command ([commands-queries-validation.md](commands-queries-validation.md#request-idempotency)) keeps its keys in a table `pouryakit_idempotency_keys` of its own schema, outside its model. Add it once with `dotnet ef migrations add IdempotencyKeys` and the Module's options, as above: the model did not change, so the migration is empty and the snapshot stays as it was; fill its `Up` with `migrationBuilder.CreateIdempotencyTable("catalog")` and its `Down` with `migrationBuilder.DropIdempotencyTable("catalog")` (`PouryaKit.Persistence.EFCore`, the Module's schema). Nothing checks it at startup: without it, the first opted-in command with a request id throws an `InvalidOperationException` that names the call, and nothing commits. The table's shape changed during Wave 2, before any release: `CreateIdempotencyTable` now also creates `reserved_at` and its index. A local database migrated from an earlier Wave 2 build has the table without them, and the first idempotent command throws an `InvalidOperationException` that names the column and its fix: drop the table and create it again with `CreateIdempotencyTable`, or run `alter table catalog.pouryakit_idempotency_keys add column reserved_at timestamp with time zone not null default now(); alter table catalog.pouryakit_idempotency_keys alter column reserved_at drop default; create index ix_pouryakit_idempotency_keys_reserved_at on catalog.pouryakit_idempotency_keys (reserved_at);` with the Module's schema.
- The Host applies no migration at startup. `HostFixture` applies every Module's in tests.

## Idempotency key retention

- The Host keeps an idempotency key for its retention, then removes it, and a repeat of its request runs as a new one. One background service, added once whatever the number of Modules, removes from every Module's `pouryakit_idempotency_keys` the keys reserved longer ago than the retention, every cleanup interval, a batch of keys per statement until a batch is short. It runs on the Host's `TimeProvider`, in a scope and on connections of its own, never inside a request or a Unit of Work, and skips a Module without the table.
- `PouryaKitIdempotencyOptions` (`PouryaKit.Persistence.EFCore`) sets it for the whole Host: `Retention` 24 hours, `CleanupInterval` 1 hour and `BatchSize` 1000 by default. A key replays for at least its retention and is removed by the first cleanup after it, so choose a retention longer than any client keeps retrying. Configure it in the Host, for example from configuration:

```csharp
builder.Services.Configure<PouryaKitIdempotencyOptions>(builder.Configuration.GetSection("PouryaKit:Idempotency"));
```

- `Retention = Timeout.InfiniteTimeSpan` keeps every key and runs no cleanup. The Host fails to start unless the retention is positive and at most 36500 days, or infinite, the interval from 1 ms to about 49.7 days, the range of `PeriodicTimer`, and the batch size at least 1.
- A repeat whose expired key a cleanup removes while the repeat is being reserved runs as a new request.
- A cleanup that removed keys logs how many (`IdempotencyKeysRemoved`); one that fails is logged at Error level (`IdempotencyKeyCleanupFailed`), and the next one tries again.
- To test it, build the Host with the test clock as its `TimeProvider`: advancing the clock past the interval runs a cleanup, which removes the keys the clock has aged past the retention.
