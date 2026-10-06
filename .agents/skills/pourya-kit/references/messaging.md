# Messaging

`PouryaKit.Messaging`, `PouryaKit.Messaging.Wolverine`, `PouryaKit.Messaging.Wolverine.EFCore`: Notification Handlers that run after the commit, the Integration Events they publish for other Modules, and the Snapshots of a Module's aggregates that the Unit of Work publishes for other Modules, each delivery durable, retried and recorded on its own, on Wolverine with its message store in PostgreSQL. Feature code uses `PouryaKit.Messaging` only. The Composition Root uses `PouryaKit.Messaging.Wolverine`; the Host and the Receipt and Snapshot version migrations use `PouryaKit.Messaging.Wolverine.EFCore`, which brings the other two, so a Module that declares Notification Handlers, publishes Snapshots or subscribes Integration Event Handlers references it. A Contracts project references nothing from the Kit.

- [Notification Handler or Domain Event Handler](#notification-handler-or-domain-event-handler)
- [Writing a Notification Handler](#writing-a-notification-handler)
- [Declaring handlers and running deliveries](#declaring-handlers-and-running-deliveries)
- [Integration Events between Modules](#integration-events-between-modules)
- [The Integration Event](#the-integration-event)
- [Declaring what a Module publishes and subscribes](#declaring-what-a-module-publishes-and-subscribes)
- [Publishing from a Notification Handler](#publishing-from-a-notification-handler)
- [Writing an Integration Event Handler](#writing-an-integration-event-handler)
- [Snapshots between Modules](#snapshots-between-modules)
- [The Snapshot](#the-snapshot)
- [Writing a Snapshot mapper](#writing-a-snapshot-mapper)
- [Declaring a Snapshot](#declaring-a-snapshot)
- [Keeping a copy](#keeping-a-copy)
- [What the Kit guarantees for a Snapshot](#what-the-kit-guarantees-for-a-snapshot)
- [The rules of a mapper and a key](#the-rules-of-a-mapper-and-a-key)
- [What the Kit guarantees](#what-the-kit-guarantees)
- [Receipts](#receipts)
- [Names](#names)
- [Retries](#retries)
- [Dead letters](#dead-letters)
- [The message store](#the-message-store)
- [The stale-time rule](#the-stale-time-rule)
- [Startup checks](#startup-checks)

A Notification Handler and the port it reaches outside the Module through, from the Proving Sample's Products Business Area:

```csharp
using PouryaKit.Domain;
using PouryaKit.Messaging;

// Products/Events/Notifications/: runs after the product is committed, never inside its transaction, because an
// announcement of a product that was then rolled back could not be taken back
internal sealed class AnnounceNewProductNotificationHandler(INewProductAnnouncer announcer)
    : INotificationHandler<ProductCreatedDomainEvent>
{
    public async Task<Result> Handle(ProductCreatedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        await announcer.AnnounceAsync(
            new NewProductAnnouncement(domainEvent.ProductId.Value, domainEvent.Name),
            cancellationToken
        );
        return Result.Success();
    }
}

// The port: public, because the Host implements it. The Module decides what is said; the Host decides how it is sent.
public interface INewProductAnnouncer
{
    Task AnnounceAsync(NewProductAnnouncement announcement, CancellationToken cancellationToken);
}

public sealed record NewProductAnnouncement(Guid ProductId, string Name);

// In the Composition Root: module.AddPouryaKitNotificationHandlers(typeof(CatalogModule).Assembly, typeof(CatalogModule));
```

## Notification Handler or Domain Event Handler

Both react to a Domain Event the Module raised. Choose by when the reaction may happen (G.2):

- A Domain Event Handler runs inside the Operation's Unit of Work, before the commit, and its changes commit with the change or not at all. Use it to keep the Module's own aggregates consistent with each other ([persistence.md](persistence.md#domain-event-handlers)).
- A Notification Handler runs after the commit, in an Operation of its own. Use it for anything that reaches outside the Module's own data and cannot be taken back, such as sending an email, pushing to a client, calling another system or publishing an Integration Event for other Modules. Its failure is retried; it never rolls back the change, which is already committed.
- One Domain Event may have both, as the sample's `ProductCreatedDomainEvent` does: `CountProductInCategoryHandler` counts the product in the same commit, and afterwards `AnnounceNewProductNotificationHandler` announces it and `PublishProductCreatedNotificationHandler` tells other Modules about it. A Domain Event needs no Domain Event Handler to be delivered as a Notification.

## Writing a Notification Handler

- Implement `INotificationHandler<TDomainEvent>` (`PouryaKit.Messaging`) for one concrete Domain Event type, in an `internal sealed` class of the Business Area that raises it, under `Events/Notifications/`. The handler and its Domain Event stay `internal`. It runs only for a Domain Event of exactly its type, never for one derived from it.
- Return a Result. A failed Result is a failure like an exception: the delivery is retried ([Retries](#retries)), then set aside as a dead letter that names each Error ([Dead letters](#dead-letters)).
- Reach outside the Module through a port: an interface the Module declares beside the handler, public so the Host can implement it, as `INewProductAnnouncer` above. The Host registers the implementation (below). The handler never names a transport, an SMTP client or another Module's types.
- Inject what a command handler may: the `OperationContext` (a continuation of the raising Operation), the Module's repositories, `IDispatcher`, and `IIntegrationEventPublisher` to tell other Modules ([Publishing from a Notification Handler](#publishing-from-a-notification-handler)). A change made through the Module's repositories commits in the handler's own Unit of Work, with its Receipt, and a Domain Event it raises is handled as in any Operation. A change that must commit with the change that raised the Domain Event belongs in a Domain Event Handler instead.
- Never save, commit, begin a transaction, touch the Outbox or reference a Wolverine type: the delivery and its Unit of Work own all of that. A structure test rejects a Module or a Contracts project whose code names any Wolverine or JasperFx type, and a Module DbContext that maps the message store's envelope tables ([testing.md](testing.md#structure-tests)).

## Declaring handlers and running deliveries

- In each Module's Composition Root, call `module.AddPouryaKitNotificationHandlers(typeof(CatalogModule).Assembly, typeof(CatalogModule))` (`PouryaKit.Messaging.Wolverine`). It finds every Notification Handler in the assembly, internal ones included, subscribes each one to its Domain Event under its subscription name ([Names](#names)), and runs its deliveries in the Module named by the second argument, the Module's definition type, so they reach that Module's Unit of Work. It throws when the assembly has no Notification Handler. An application without Modules calls it on its own services and leaves the Module out.
- That Module needs its own Unit of Work (`UsePostgres`, [persistence.md](persistence.md#postgresql)) and the Receipt table ([Receipts](#receipts)).
- In the Host, call `builder.UsePouryaKitWolverine(connectionName: "app")` (`PouryaKit.Messaging.Wolverine.EFCore`) once; Modules added after it still get their subscriptions. It runs the Host's deliveries, of Notification Handlers and Integration Event Handlers alike, on Wolverine with its message store in the database of that connection string, which must be the one the Modules' DbContexts use: the Outbox is written in their transactions. Wolverine creates its tables in the store schema when the Host starts. Wolverine discovers no handlers of its own.
- The Host project, not the Kit, references `WolverineFx.RuntimeCompilation`: Wolverine 6 compiles its runtime code with it, and no Kit package references it (ADR 0004).

```xml
<!-- ModularMonolith.Host.csproj: Wolverine 6 compiles its runtime code with Roslyn from this package -->
<PackageReference Include="WolverineFx.RuntimeCompilation" />
```

The Host implements the port and registers it, beside the deliveries:

```csharp
using Catalog.Products.Events.Notifications;
using Microsoft.Extensions.Logging;

// ModularMonolith.Host: announces in the log, where a real application would send it to its customers
public sealed partial class LoggedNewProductAnnouncer(ILogger<LoggedNewProductAnnouncer> logger) : INewProductAnnouncer
{
    public Task AnnounceAsync(NewProductAnnouncement announcement, CancellationToken cancellationToken)
    {
        LogAnnounced(logger, announcement.Name, announcement.ProductId);
        return Task.CompletedTask;
    }

    [LoggerMessage(EventName = "Announced", Level = LogLevel.Information, Message = "New in the catalog: {Name} ({ProductId})")]
    private static partial void LogAnnounced(ILogger logger, string name, Guid productId);
}

// In ModularMonolithHost.CreateBuilder, after AddPouryaKitModules (web.md):
//     builder.UsePouryaKitWolverine(connectionName: "app");
//     builder.Services.AddSingleton<INewProductAnnouncer, LoggedNewProductAnnouncer>();
```

## Integration Events between Modules

Across Modules, a Module tells the others what happened with an Integration Event (G.5): use one when another Module reacts to something that happened in this one. When another Module keeps its own copy of this one's data, publish a Snapshot instead ([Snapshots between Modules](#snapshots-between-modules)). The receiving Module applies it later, in an Operation of its own, and the publishing Operation never waits for it. A synchronous question through the owning Module's Contracts ([modules.md](modules.md#asking-another-module-a-question)) is the exception, for an Operation that cannot go on without the answer.

In the Proving Sample, Catalog tells Inventory and Billing about each new product: Inventory opens a stock item for it, and Billing a price list:

1. `CreateProduct` commits the product; its `ProductCreatedDomainEvent` is stored as a Notification in the same commit.
2. After that commit, Catalog's `PublishProductCreatedNotificationHandler` publishes `ProductCreatedV1`, which is stored in Catalog's Outbox in the commit of that delivery, once per subscription.
3. After that commit, Inventory's `OpenStockItemIntegrationEventHandler` opens the stock item in Inventory's Unit of Work, and Billing's `OpenPriceListIntegrationEventHandler` opens the price list in Billing's, each in a delivery of its own.

### The Integration Event

```csharp
// Catalog.Contracts/Products/Events/V1/: public, plain values only, nothing from the Kit
/// <summary>
/// A product was added to the catalog. <paramref name="Id"/> and <paramref name="OccurredOn"/> are the identity and the
/// time of the Domain Event it follows from, the same in every delivery. Catalog publishes it under the wire name
/// <c>catalog.product-created.v1</c>; a change other Modules could not read is a new version, not an edit of this one.
/// </summary>
public sealed record ProductCreatedV1(Guid Id, DateTimeOffset OccurredOn, Guid ProductId, string Name);
```

- Declare it as a `public sealed record` in the publishing Module's Contracts project, under `Events/V1/`, named with its version. It holds plain values, such as `Guid`, strings, numbers, times and records of them, never a typed ID or another of the Module's internal types. It carries no attribute, because the Contracts project references nothing from the Kit: its wire name is declared where the Module publishes it ([Names](#names)).
- Give it the Domain Event's `Id` and `OccurredOn`, as `Id` and `OccurredOn`, so the receiver sees the identity and time the publisher saw.
- A change that receivers could not read is a new type with its own version and wire name, never an edit of the old one.

### Declaring what a Module publishes and subscribes

The receiving Module's Composition Root, from the Proving Sample:

```csharp
using Catalog.Contracts.Products.Events.V1;
using Inventory.CatalogProducts.Features.CopyProduct;
using Inventory.Infrastructure.Data.Context;
using Inventory.StockItems.Features.OpenStockItem;
using PouryaKit.Cqrs;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;
using PouryaKit.Persistence.Postgres;

// Inventory/InventoryModule.cs
public sealed class InventoryModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module)
    {
        module.UsePostgres<InventoryDbContext>(connectionName: "app", schema: "inventory");
        module.AddPouryaKitCqrs(typeof(InventoryModule).Assembly);
        module.Subscribe<ProductCreatedV1, OpenStockItemIntegrationEventHandler>(
            "inventory.product-created",
            typeof(InventoryModule)
        );
        // Catalog's Snapshot of each product, for Inventory's copy (Snapshots between Modules, below).
        module.Subscribe<ProductSnapshotV1, CopyProductIntegrationEventHandler>(
            "inventory.catalog-products",
            typeof(InventoryModule)
        );
    }
}

// In Catalog's Composition Root, the publishing side (modules.md):
//     module.Publish<ProductCreatedV1>("catalog.product-created.v1", typeof(CatalogModule));
//     module.PublishSnapshot<Product, ProductSnapshotV1, ProductSnapshotMapper>("catalog.product-snapshot.v1", typeof(CatalogModule));
// In Billing's Composition Root, a second subscription to the same Integration Event, under its own name:
//     module.Subscribe<ProductCreatedV1, OpenPriceListIntegrationEventHandler>("billing.product-created", typeof(BillingModule));
```

- The publishing Module declares each Integration Event it publishes with `module.Publish<TIntegrationEvent>(wireName, typeof(CatalogModule))` (`PouryaKit.Messaging.Wolverine`). One Module publishes a type, under one wire name.
- The receiving Module subscribes each Integration Event Handler with `module.Subscribe<TIntegrationEvent, THandler>(subscriptionName, typeof(InventoryModule))`. The subscription name is declared, not derived, and the handler's deliveries run in the Module the last argument names, so they reach its Unit of Work. `Subscribe` registers the handler, which may be `internal`.
- Several Modules may subscribe to one Integration Event, each with its own `Subscribe` and subscription name, as Inventory and Billing do to `ProductCreatedV1`. The publisher changes nothing for a new subscription: each gets its own delivery, and one that fails never makes another run again ([What the Kit guarantees](#what-the-kit-guarantees)).
- The receiving Module needs its own Unit of Work (`UsePostgres`) and the Receipt table in its schema ([Receipts](#receipts)).
- The receiving Module's project references the publishing Module's Contracts project and never the publishing Module: Inventory references `Catalog.Contracts` only. Neither Module references a Wolverine type.
- The Host adds both Modules and calls `UsePouryaKitWolverine(connectionName: "app")` once, as for Notification Handlers ([web.md](web.md#the-host)).
- An application without Modules calls `Publish` and `Subscribe` on its own services and leaves the Module out.

### Publishing from a Notification Handler

```csharp
using Catalog.Contracts.Products.Events.V1;
using PouryaKit.Domain;
using PouryaKit.Messaging;

// Catalog/Products/Events/Notifications/: tells other Modules about the new product once it is committed. The
// Integration Event carries the Domain Event's identity and time, so every receiver recognises a redelivery.
internal sealed class PublishProductCreatedNotificationHandler(IIntegrationEventPublisher events)
    : INotificationHandler<ProductCreatedDomainEvent>
{
    public Task<Result> Handle(ProductCreatedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        events.Publish(
            domainEvent.Id,
            new ProductCreatedV1(domainEvent.Id, domainEvent.OccurredOn, domainEvent.ProductId.Value, domainEvent.Name)
        );
        return Task.FromResult(Result.Success());
    }
}
```

- Publish from a Notification Handler only (G.2): inject `IIntegrationEventPublisher` (`PouryaKit.Messaging`) and call `Publish(domainEvent.Id, integrationEvent)` with the `Id` of the Domain Event the handler is handling. Publishing sends nothing: the delivery's Unit of Work writes the Integration Event to the Module's Outbox when it commits, and it is sent after that commit.
- Give publishing a Notification Handler of its own, as the sample keeps it apart from the announcement: each handler is retried on its own, so a failing announcement neither holds back nor repeats the Integration Event.
- `Publish` throws `InvalidOperationException` when it is called:
  - outside a Notification Handler's delivery, such as in a command handler or in an Integration Event Handler;
  - with an identity other than the `Id` of the Domain Event being delivered;
  - with a type the running Module did not declare with `Publish`, another Module's Integration Event included;
  - with a type declared as a Snapshot: the Unit of Work publishes it, and nothing publishes one by hand ([Snapshots between Modules](#snapshots-between-modules));
  - a second time with the same type in one Notification Handler's delivery, because both would carry one identity and every receiver would apply only the first;
  - in [code that only reads](commands-queries-validation.md#code-that-only-reads), even a query or validator the Notification Handler runs.

  In a Notification Handler, the exception fails the delivery, which is retried and then set aside as a dead letter.
- One Domain Event yields at most one Integration Event of each type, from one Notification Handler. When two Notification Handlers of one Domain Event publish the same type, the second delivery to commit fails, and keeps failing until it is set aside as a dead letter: the Outbox records each publication in the publishing Module's Receipt table, keyed by the wire name and the Domain Event's `Id`.
- An Integration Event that no Module of the Host subscribes to is not stored: it has no queue to go to.
- Never publish a Domain Event: it stays inside its Module, and `Publish<T>` refuses one.

### Writing an Integration Event Handler

```csharp
using Catalog.Contracts.Products.Events.V1;
using Inventory.StockItems.Entities;
using Inventory.StockItems.ValueObjects;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Persistence;

// Inventory/StockItems/Features/OpenStockItem/: only the effect. The Kit records the Receipt, commits and acknowledges,
// so a redelivered or concurrently duplicated ProductCreatedV1 opens one stock item.
internal sealed class OpenStockItemIntegrationEventHandler(IRepository<StockItem, StockItemId> stockItems)
    : IIntegrationEventHandler<ProductCreatedV1>
{
    public async Task<Result> Handle(ProductCreatedV1 integrationEvent, CancellationToken cancellationToken)
    {
        await stockItems.AddAsync(StockItem.Open(integrationEvent.ProductId, integrationEvent.OccurredOn), cancellationToken);
        return Result.Success();
    }
}
```

- Implement `IIntegrationEventHandler<TIntegrationEvent>` (`PouryaKit.Messaging`) in an `internal sealed` class of the receiving Module, beside the Feature it serves, and subscribe it in the Composition Root (above).
- Hold only the business effect, through the Module's repositories, and return a Result. A failed Result is a failure like an exception: the delivery is retried ([Retries](#retries)), then set aside as a dead letter of its subscription ([Dead letters](#dead-letters)), and nothing the attempt changed commits, its Receipt included.
- Inject what a command handler may: the `OperationContext`, the Module's repositories, `IDispatcher`. Read when the fact happened from the Integration Event, as `StockItem.Open` takes `OccurredOn`; the Operation's own clock gives the time of the delivery.
- Never save, commit, begin a transaction, record a Receipt, acknowledge or reference a Wolverine type, and never publish: an Integration Event is published only from a Notification Handler.

## Snapshots between Modules

A Snapshot is the current state of one aggregate, published for other Modules, and it supersedes every earlier Snapshot of that aggregate. A Tombstone is the Snapshot that says the aggregate no longer exists. Choose between a Snapshot and an Integration Event by what the receiver needs (G.2, G.5):

- An Integration Event is a fact: something happened, and the receiver reacts to it, as Inventory opens a stock item for each new product. Each one matters on its own, and a Notification Handler publishes it.
- A Snapshot is state: what the aggregate is now, for a Module that keeps its own copy of it, as Inventory keeps a copy of each catalog product. Only the newest matters: once a newer one is applied, an older one is worth nothing. Feature code never publishes it; the Unit of Work does, in the commit of every change of the aggregate.
- One aggregate may have both, as Catalog's product does: `ProductCreatedV1` for what happened, `ProductSnapshotV1` for what it is.

In the Proving Sample, Inventory keeps a copy of Catalog's products:

1. Every Operation of Catalog that creates, renames or removes a product stores the product's `ProductSnapshotV1` in Catalog's Outbox, in the commit of that change, once per subscription.
2. After that commit, Inventory's `CopyProductIntegrationEventHandler` writes it into Inventory's copy, in Inventory's Unit of Work, when it is newer than every Snapshot of that product Inventory applied.

### The Snapshot

```csharp
// Catalog.Contracts/Products/Events/V1/: public, plain values only, nothing from the Kit
/// <summary>
/// The current state of one product, which supersedes every earlier Snapshot of it: <paramref name="Version"/> grows
/// with every change Catalog commits. When <paramref name="IsRemoved"/> is true it is the product's Tombstone: the
/// product has left the catalog. Catalog publishes it under the wire name <c>catalog.product-snapshot.v1</c>; a change
/// other Modules could not read is a new version, not an edit of this one.
/// </summary>
public sealed record ProductSnapshotV1(Guid ProductId, string Name, bool IsRemoved, long Version);
```

- Declare it as a `public sealed record` in the publishing Module's Contracts project, under `Events/V1/`, named with its version, with plain values only, as an Integration Event ([The Integration Event](#the-integration-event)).
- Give it the aggregate's key, the state other Modules need, a field that says whether it is the Tombstone, as `IsRemoved` does, and the aggregate's `Version`. The Kit orders Snapshots by the version it sends beside the message, not by these fields; the sample carries the version too, so its copy shows which one it holds.
- A type is either an Integration Event or the Snapshot of one aggregate: declaring it with `Publish` and with `PublishSnapshot`, or with two `PublishSnapshot` declarations that differ in aggregate or mapper, fails the Host's composition, and `IIntegrationEventPublisher.Publish` refuses a Snapshot.
- A change that receivers could not read is a new type with its own version and wire name, never an edit of the old one.

### Writing a Snapshot mapper

```csharp
using Catalog.Contracts.Products.Events.V1;
using PouryaKit.Messaging;

// Catalog/Products/Events/: the product as other Modules see it. The Kit calls it once for every product an
// Operation changed, after every reaction ran, with the version being committed; it only reads.
internal sealed class ProductSnapshotMapper : ISnapshotMapper<Product, ProductSnapshotV1>
{
    public ProductSnapshotV1 Map(Product product) => new(product.Id.Value, product.Name, IsRemoved: false, product.Version);

    public ProductSnapshotV1 Tombstone(Product product) => new(product.Id.Value, product.Name, IsRemoved: true, product.Version);
}
```

- Implement `ISnapshotMapper<TAggregate, TSnapshot>` (`PouryaKit.Messaging`) in an `internal sealed` class of the aggregate's Business Area, under `Events/`. It has two methods: `Map` builds the Snapshot of an aggregate that exists, and `Tombstone` builds the Snapshot of one the Operation removed, whether it is kept marked deleted or deleted outright. Both receive the aggregate in its final state.
- Map by hand, member by member, with no mapping library (G.7). Read the version from the aggregate's `Version`: it is the version being committed.
- `PublishSnapshot` registers the mapper scoped, and the Kit resolves it in the scope of the Operation that commits and runs it as a step of the Unit of Work that only reads. It reads the aggregate, and may run a query through an injected `IDispatcher` when it needs more; it writes nothing. The sample's takes nothing. [The rules of a mapper and a key](#the-rules-of-a-mapper-and-a-key) say what is refused and what fails the Operation.

### Declaring a Snapshot

- The publishing Module declares each Snapshot in its Composition Root: `module.PublishSnapshot<Product, ProductSnapshotV1, ProductSnapshotMapper>("catalog.product-snapshot.v1", typeof(CatalogModule))` (`PouryaKit.Messaging.Wolverine`). It names the aggregate, the Snapshot type and the mapper, then the wire name and the Module whose Operations publish it ([modules.md](modules.md#the-composition-root)). One aggregate may have several Snapshot types, each with its own declaration, wire name and mapper.
- The receiving Module subscribes an ordinary `IIntegrationEventHandler<TSnapshot>` with `Subscribe`, as Inventory does above ([Declaring what a Module publishes and subscribes](#declaring-what-a-module-publishes-and-subscribes)). Several Modules may subscribe, each under its own subscription name.
- A Snapshot's wire name and its subscriptions' names are declared, never derived, and follow [Names](#names) like an Integration Event's.
- An application without Modules calls `PublishSnapshot` on its own services and leaves the Module out.

### Keeping a copy

```csharp
using Catalog.Contracts.Products.Events.V1;
using Dapper;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Persistence;

// Inventory/CatalogProducts/Features/CopyProduct/: keeps Inventory's copy of each catalog product current, in a table
// of its own that is no aggregate. Only the effect: the Kit runs this only for a Snapshot newer than every one applied
// of that product, so the copy simply follows each one, and is deleted on the Tombstone.
internal sealed class CopyProductIntegrationEventHandler(IUnitOfWorkConnection unitOfWork)
    : IIntegrationEventHandler<ProductSnapshotV1>
{
    public async Task<Result> Handle(ProductSnapshotV1 snapshot, CancellationToken cancellationToken)
    {
        var (connection, transaction) = unitOfWork.Join();
        var sql = snapshot.IsRemoved
            ? "delete from inventory.catalog_products where product_id = @ProductId"
            : """
              insert into inventory.catalog_products (product_id, name, version) values (@ProductId, @Name, @Version)
              on conflict (product_id) do update set name = excluded.name, version = excluded.version
              """;
        await connection.ExecuteAsync(new CommandDefinition(sql, snapshot, transaction, cancellationToken: cancellationToken));
        return Result.Success();
    }
}
```

- Keep the copy in a Module-internal table that no aggregate maps, written with plain SQL through `IUnitOfWorkConnection` in the receiving Module's Unit of Work ([dapper-reads.md](dapper-reads.md#writing-with-plain-sql-in-the-unit-of-work)), so the copy, the Receipt and the version the Kit records commit together or not at all.
- The copy is not an aggregate. It holds what the publishing Module last published and nothing the receiving Module decides: it has no rule to guard, raises no Domain Event, and needs no version of its own, because the Kit already decides which Snapshot is applied. Inventory's own decisions about a product live in its own aggregates, such as the stock item.
- Write only the effect: insert or update for a Snapshot, delete for a Tombstone. The handler compares no version: the Kit runs it only for a newer one, and after a Tombstone it runs for no older one, so a deleted row stays deleted.
- Inventory's project references `PouryaKit.Persistence.Dapper`, which brings Dapper for `ExecuteAsync` and `CommandDefinition`. It does not call `AddPouryaKitDapper`: that registers Dapper reads and typed-ID handlers, and the copy needs neither. `IUnitOfWorkConnection` comes with `UsePostgres`.

The receiving Module's migration creates the copy and the table the Kit keeps the applied versions in, `CreateSnapshotVersionTable` (`PouryaKit.Messaging.Wolverine.EFCore`), beside the Receipt table it already has ([Receipts](#receipts)). Neither is in the Module's model, so the model does not change; add the migration with `dotnet ef migrations add` as for the Receipt table and fill its `Up` and `Down`:

```csharp
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PouryaKit.Messaging.Wolverine.EFCore;

// Inventory/Infrastructure/Data/Migrations/: the copy, written with plain SQL, and the versions of the Snapshots applied
[DbContext(typeof(InventoryDbContext))]
[Migration("20261005190000_CatalogProducts")]
internal sealed class CatalogProducts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "create table inventory.catalog_products (product_id uuid primary key, name text not null, version bigint not null)"
        );
        migrationBuilder.CreateSnapshotVersionTable("inventory");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropSnapshotVersionTable("inventory");
        migrationBuilder.Sql("drop table inventory.catalog_products");
    }
}
```

- A Module that subscribes to any Snapshot needs both tables in its schema, each created once; [Receipts](#receipts) says which Module needs which table and what fails without one.
- A version row is not a Receipt: it holds, for each subscription and aggregate, the newest version applied, and is what keeps an older Snapshot from undoing a newer one or a Tombstone. The Kit removes no row from it, and nothing should.

### What the Kit guarantees for a Snapshot

- **One per changed aggregate, per Snapshot type, per Operation.** Each Operation of the declaring Module that changes an aggregate, or anything the aggregate owns, publishes one Snapshot of it under each Snapshot type declared for it, however many times the Operation changed it: in the handler, in a nested command and in a Domain Event Handler, it is still one. That holds for every Operation of the Module, a delivery's included, as when one of its Notification Handlers changes the aggregate. An Operation that changes nothing, or rolls back, publishes none.
- **Built from what commits.** The Unit of Work builds it after every Domain Event Handler ran and persistence is prepared (step 4 of its commit sequence, [persistence.md](persistence.md#the-unit-of-work)): the aggregate is in its final state, audit and soft-delete fields set, and its `Version` is the one being committed.
- **Stored in the commit, sent after it.** It is written to the publishing Module's Outbox, once per subscription, in the same transaction as the change, so it exists if and only if the change commits, and it is sent after the commit, as a Notification is ([What the Kit guarantees](#what-the-kit-guarantees)). A Snapshot type that no Module of the Host subscribes to is not stored.
- **A Tombstone with a newer version.** An aggregate the Operation removed, whether soft-deletable and kept marked deleted or deleted outright, publishes the mapper's `Tombstone` instead, with the version the removal advanced it to, one newer than its last Snapshot.
- **Applied only when newer.** Each Snapshot carries its aggregate's key, as the database stores it, and its version, in the `SnapshotHeaders.AggregateId` and `SnapshotHeaders.Version` headers. Its identity follows from its wire name, key and version, so every delivery of one Snapshot has the same identity and two versions never share one. The receiving delivery first records the Receipt, so a redelivery or a concurrent duplicate of the same Snapshot does nothing. It then records the version, and the handler runs only when that version is strictly newer than every version of that aggregate the subscription applied before; otherwise the delivery succeeds without running it. A concurrent delivery of another version of the same aggregate waits until the first commits or rolls back, then compares with what committed. If the older version commits first, both handlers can run in increasing version order; if the newer commits first, the older is skipped. So Snapshots delivered in reverse order, concurrently or twice, and a live Snapshot delivered after the Tombstone, all leave the receiver's copy at the newest version. The version is recorded in the handler's transaction, with its effect and Receipt, or not at all.
- **Not every version.** A receiver applies the newest Snapshot it is given and skips the older ones it gets later: a Snapshot handler keeps a copy current, and never counts or reacts to each change. React to what happened with an Integration Event.
- **A continuation of the change.** The receiving Operation continues the Operation that committed the change: the same Actor, tenant and correlation, that Operation's `OperationId` as its causation, a new `OperationId`.
- **Each subscription on its own.** Each subscription's delivery is retried, recorded and set aside as a dead letter apart from the others, as for an Integration Event ([Retries](#retries), [Dead letters](#dead-letters)).

### The rules of a mapper and a key

A feature author keeps these rules, or the copies in other Modules go wrong:

- **A mapper only reads.** It runs after every Domain Event was handled and every version and audit field set, so what it changed would commit unversioned and unseen by the Snapshot, and a Domain Event it raised would never be handled. It may read the aggregate and run queries, nothing else:
  - It runs as a step of the Unit of Work that only reads, so the Kit refuses every write it attempts, as in any [code that only reads](commands-queries-validation.md#code-that-only-reads): a nested command, the repository's `AddAsync` and `Remove`, `IUnitOfWorkConnection.Join()` and publishing an Integration Event. Each refusal is an `InvalidOperationException` that fails the Operation and reaches the Gateway's caller, and nothing commits. A mapper that catches the refusal of its command still fails the Operation: the refused command marks it rollback-only, and the Unit of Work checks that once more after the mappers ran, so the Operation fails with `Operation.CommandThrew`.
  - The Unit of Work compares tracked state once after all mappers finish against the state before the batch. The shared EF comparison includes complex collections recursively and pending Domain Event identity and order. A difference throws an `InvalidOperationException` during commit and rolls back the transaction. This comparison cannot detect a transient change restored before the batch ends. Queries a mapper calls retain their individual read guards.
- **The aggregate's key is set by the application, not generated by the database.** The Snapshot is built before the save, and a key the database generates does not exist until then. Nothing checks it at startup: an Operation that creates such an aggregate fails at its commit with an `InvalidOperationException`, and nothing commits. Create keys as `Product` does, with `TypedId.New<ProductId>()`.
- **The provider key has a lossless encoding.** After EF's value converter, each key part must be a non-null `Guid`, `int`, `long`, `string`, `byte[]` or `DateTime`. Guid, int, long and string keep their existing invariant formats; bytes use uppercase hexadecimal. A DateTime uses its ticks without Kind, so PostgreSQL's timestamp without time zone can reload it with Unspecified Kind without changing its identity. Its mapping must be a PostgreSQL timestamp with precision from 0 to 6, and every value must align to that precision. The Kit refuses date mappings, submicrosecond values and any other value that the mapping would round or truncate. Composite keys prefix each encoded part with its invariant length and a colon. Null and other provider types fail the Operation before saving. A converter to another supported provider type must preserve the key across reloads.
- **Old colliding identities need a deliberate recovery.** Earlier byte-array keys all encoded as `System.Byte[]`, and DateTime formatting lost fractions of a second. Existing Receipts and applied-version rows cannot be migrated losslessly because those identities do not identify the original key. If such keys were deployed, drain their old deliveries and deliberately rebuild their projections, Receipts and applied-version state from the publishing data before using the corrected encoding. Guid, int, long and string identities are unchanged.
- **The key of an aggregate that publishes Snapshots is never used again after the aggregate is deleted outright.** Every receiver keeps its Tombstone's version and would turn away a new aggregate's lower versions under the same key until they passed it. The Kit's Guid version 7 keys are never used again. A soft-deleted aggregate keeps its key and its version, and the repository never returns it again; a key a person chooses, such as a code, is the one to watch.
- **A declaration names its Module and covers one aggregate.** Snapshots are built only in Operations of the Module the declaration names, so in a Host of Modules pass it the Module, and in an application without Modules leave it out. A declaration without its Module in a Host of Modules, or with one in an application without Modules, would never publish: it fails the Operation that changes its aggregate with an `InvalidOperationException` instead, and nothing commits. A declaration of one Module never publishes for another Module's aggregates. Declare it for one aggregate, or for a type of one mapped inheritance hierarchy, never for a base type that independent aggregates share: their keys are their own, so their Snapshots would share identities and versions, and a receiver would turn one aggregate's away for another's. Such a declaration is refused the same way, by the first Operation of the Module that commits a change to any aggregate the Module declares a Snapshot for.

## What the Kit guarantees

- **Stored in the commit.** The Unit of Work writes each Domain Event the Operation raised that a Notification Handler subscribes to into the Outbox, once per subscription, after it prepares persistence and before it saves, in the same transaction. The Notification exists if and only if the change commits: a failed Result, a failed Domain Event Handler, a refused save or an exception leaves none. A Domain Event that no Notification Handler subscribes to stores nothing.
- **Sent after the commit.** The Unit of Work sends what it wrote only after the commit. When the send fails, or the Host dies first, the Notification stays in the store: a Host that starts recovers it, and a live Host hands it out again after `InboxStaleTime` ([The message store](#the-message-store)). The Operation's Result is unaffected, because its change is committed; the failed send is logged as a warning.
- **A continuation in its own scope.** Each delivery attempt runs in a new scope, as an Operation of the subscribing Module, begun with a continuation of the raising Operation's context: the same Actor, tenant and correlation, the raising Operation's `OperationId` as its causation, a new `OperationId`. It runs through the dispatcher, with the Module's Unit of Work and the Host's clock, and nothing from a failed attempt reaches the next.
- **Each handler on its own.** Each Notification Handler of a Domain Event has its own stored delivery, retried, recorded, acknowledged and set aside as a dead letter apart from the others, so a handler that fails never makes one that succeeded run again.
- **A database effect once.** Before the handler runs, the delivery records a Receipt, the subscription name and the Domain Event's `Id`, in the handler's transaction, so the Receipt commits with the handler's effect or not at all. A redelivery after that commit, such as after a crash before the store marked the delivery handled, finds the Receipt and does nothing. A concurrent copy of the same delivery waits until the first commits, then does nothing; if the first rolled back, the copy runs. A handler whose effect is in its Module's database needs nothing more to be safe to run again.
- **An outside effect at least once.** An effect outside the database, such as an email, cannot roll back with the Receipt. When the attempt that sent it fails or dies before its Receipt commits, the next attempt sends it again. Make a repeat harmless where the outside system allows it, for example by passing it the Domain Event's `Id` as an idempotency key, and do the outside effect last.

An Integration Event is delivered the same way, sent after the commit, in a new scope per attempt, on its own and applied once, and also:

- **Stored with the publishing handler's Receipt.** It is written to the publishing Module's Outbox, once per subscription, in the commit of the Notification Handler's delivery that published it, together with that handler's Receipt and effect. A delivery that fails after publishing leaves no Integration Event; only the attempt that commits stores one.
- **One durable delivery per subscription**, on the subscription's own queue, retried, recorded, acknowledged and set aside as a dead letter apart from every other. When several Modules subscribe to one Integration Event, a subscription that keeps failing never runs one that succeeded again: not through its retries, not when it is set aside or replayed, and not when its Host dies during it and another Host takes it over.
- **Applied once.** Its identity is the `Id` of the Domain Event it follows from, carried by every delivery of it in the `IntegrationEventHeaders.DomainEventId` header. The receiving handler's Receipt, the subscription name and that identity, commits together with its effect, so a redelivery after the commit, or a concurrent duplicate, applies it once.
- **A continuation, two Operations on.** The receiving Operation continues the publishing Notification Handler's delivery: the same Actor, tenant and correlation as the Operation that made the change, the delivery's `OperationId` as its causation, a new `OperationId`. No HTTP request is involved.
- **The same identity and time at both ends.** The Kit enforces the identity: `Publish` refuses any other. The time is the same when the Integration Event carries the Domain Event's `OccurredOn`, as `ProductCreatedV1` does.

## Receipts

- A Module keeps its handlers' Receipts in a table `pouryakit_receipts` in its own schema, keyed by the subscription name, lower-cased, and the identity of what was delivered: a Notification's or Integration Event's is the Domain Event's `Id`; a Snapshot's is derived from its wire name, its aggregate's key and its version. The table is not part of the Module's model, so a migration creates it with `CreateReceiptTable` and the Module's schema.
- Which Module needs which table:
  - `pouryakit_receipts` (`CreateReceiptTable`): every Module that declares a Notification Handler, subscribes an Integration Event Handler or publishes Integration Events. Inventory calls it in its first migration, beside its own tables. A publishing Module's table also holds one row per Domain Event and wire name it published, keyed `<wire name>#published`, which no subscription name can take: it is how the Kit refuses a second Notification Handler publishing the same type for one Domain Event, and it is not a Receipt.
  - `pouryakit_snapshot_versions` (`CreateSnapshotVersionTable`, [Keeping a copy](#keeping-a-copy)): every Module that subscribes to a Snapshot, beside its Receipt table.
- Nothing checks either table at startup. Without one, every delivery that needs it fails with an `InvalidOperationException` that names the call to add, and is retried and set aside as a dead letter. `HostFixture` applies every migration before it starts the Host, so a test shows the gap at once.
- The Kit removes no row from either table, and nothing should: a Receipt is what keeps a redelivery from applying twice.
- Add it with `dotnet ef migrations add Receipts` and the Module's `--project`, `--context` and `--output-dir`, as in [persistence.md](persistence.md#migrations). The model did not change, so the migration is empty and the snapshot stays as it was; fill its `Up` and `Down`:

```csharp
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PouryaKit.Messaging.Wolverine.EFCore;

// Infrastructure/Data/Migrations/
[DbContext(typeof(CatalogDbContext))]
[Migration("20261005180000_Receipts")]
internal sealed class Receipts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateReceiptTable("catalog");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropReceiptTable("catalog");
}
```

## Names

Each Notification and Integration Event is stored under its wire name, and each delivery under its handler's subscription name, which is also the name of its queue and half of its Receipt's key. Both are durable identifiers.

- **A Domain Event's wire name, by convention:** the assembly's name and the type's name without the `DomainEvent` suffix, each in kebab case: `catalog.product-created` for `ProductCreatedDomainEvent` in the assembly `Catalog`. It comes from the assembly, not from the Module.
- **A Notification Handler's subscription name, by convention:** the wire name, a dot, and the handler's name without the `NotificationHandler` or `Handler` suffix, in kebab case: `catalog.product-created.announce-new-product`.
- **Overrides:** `[WireName("...")]` on the Domain Event and `[SubscriptionName("...")]` on the Notification Handler (`PouryaKit.Messaging`) declare a stable name instead.
- **An Integration Event's and a Snapshot's names are declared, never derived:** the wire name is the first argument of `Publish` (`catalog.product-created.v1`) or `PublishSnapshot` (`catalog.product-snapshot.v1`), and each Integration Event Handler's subscription name the first argument of `Subscribe` (`inventory.product-created`). Neither depends on a CLR name, so renaming or moving the record or the handler keeps them. Put the version in the wire name, and change it only with a new version of the type.
- **One wire name per type in a process.** Wolverine keeps message names for the whole process, so a second Host in the same process, such as one a test starts, that publishes a type under another wire name fails to start.
- **A rename is a breaking change.** A derived name changes when its Domain Event, its handler or its assembly is renamed, or the type moves to another assembly; a declared one changes when its declaration does. Deliveries already stored keep the old name: those under a wire name nothing declares any more no longer find their type, those under a subscription name nothing declares arrive on a queue no subscription declares, and either is set aside as a dead letter, never run and never treated as handled. Before renaming a type or handler that uses a derived name while deliveries may be in flight, declare the name it had:

```csharp
using PouryaKit.Domain;
using PouryaKit.Messaging;

// Renamed from ProductCreatedDomainEvent and AnnounceNewProductNotificationHandler: both keep the names they had.
[WireName("catalog.product-created")]
internal sealed record ProductAddedDomainEvent(ProductId ProductId, CategoryId CategoryId, string Name) : DomainEvent;

[SubscriptionName("catalog.product-created.announce-new-product")]
internal sealed class AnnounceAddedProductNotificationHandler(INewProductAnnouncer announcer)
    : INotificationHandler<ProductAddedDomainEvent>
{
    public async Task<Result> Handle(ProductAddedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        await announcer.AnnounceAsync(new NewProductAnnouncement(domainEvent.ProductId.Value, domainEvent.Name), cancellationToken);
        return Result.Success();
    }
}
```

- **A valid name** is letters and digits in segments joined by single dots or hyphens, as above; empty, blank, a space or any other character is invalid. A subscription name may not be a queue Wolverine keeps for itself: `agents`, `default`, `durable`, `replies`, `scheduled` or `system`.
- **Case is ignored.** Queues lower-case their names, so two wire names, or two subscription names, that differ only in case are a collision, whether they belong to Domain Events, Integration Events or their handlers; so are `Shelved` and `ShelvedDomainEvent` in one assembly, whose derived wire names are the same. Changing only the case of a subscription name keeps the same subscription, Receipts included.
- A missing, invalid, reserved or colliding name fails the Host's composition ([Startup checks](#startup-checks)), naming every problem.

## Retries

- A delivery fails when its handler throws or its Operation returns a failed Result, including when nested work made it rollback-only even though the handler returned success. A failed Result is never acknowledged. Nothing the attempt changed commits, its Receipt included.
- A failed delivery is retried after each of `PouryaKitMessagingOptions.RetryDelays` (`PouryaKit.Messaging.Wolverine`; 1 s, 10 s and 1 min by default): one attempt more than there are delays, so four. Each wait is stored with the delivery, which no Host instance holds while it waits, so a restart neither loses nor repeats it.
- Each attempt runs in a new scope, as a new Operation, and nothing of the attempt before is left in it: no DbContext, tracked aggregate or scoped service of the failed attempt reaches the next.
- Only the failing subscription's delivery is retried. The other subscriptions of the same Domain Event or Integration Event are not delivered again, whether they succeeded or fail too.
- Change the delays together with the inbox stale time, never alone ([The stale-time rule](#the-stale-time-rule)).

## Dead letters

- When its attempts are used up, the delivery is set aside as a dead letter of its own subscription: its envelope leaves the Inbox for Wolverine's dead-letter storage, the table `wolverine_dead_letters` in the store's schema, and is never marked handled. Only that subscription's delivery is set aside; the others of the same message keep what they did.
- A dead letter says why. It keeps the exception of its last attempt, type and message: a handler that threw records its own exception, and one that returned a failed Result records a `NotificationHandlerFailedException` or an `IntegrationEventHandlerFailedException` (`PouryaKit.Messaging.Wolverine`), whose message names the subscription and each Error as its code and message, such as `PriceList.Closed (The price list is closed.)`. Both exceptions also carry `Subscription` and `Errors`.
- A dead letter is found by its subscription: it was received at `local://{subscription}/`, its `received_at`, with the subscription name lower-cased, and its `message_type` is the wire name.
- A stored delivery whose subscription or type no longer exists is set aside the same way, never run and never treated as handled: one to a subscription the Host no longer declares fails with `UnknownSubscriptionException`, through its retries, and one whose type no subscription of the Host handles any more is dead-lettered as an unknown message. Renaming or removing a subscription, or the last subscription to a type, while deliveries are in flight leaves those deliveries as dead letters ([Names](#names)).
- The Kit adds no dead-letter API of its own: Wolverine's dead-letter storage already finds a dead letter by its subscription and replays it, and a second way to do the same would be a second implementation of one concern. The Host reads and replays them through `IWolverineRuntime.Storage.DeadLetters`, from operator code such as an admin endpoint or a maintenance command, never from Feature code:

```csharp
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Runtime;

// ModularMonolith.Host: operator code. Feature code never references Wolverine.
public sealed class DeadLetterTriage(IWolverineRuntime runtime)
{
    // Every dead letter of one subscription; a page size of 0 reads them all.
    public async Task<IReadOnlyList<DeadLetterEnvelope>> OfAsync(string subscription, CancellationToken cancellationToken) =>
        (
            await runtime.Storage.DeadLetters.QueryAsync(
                new DeadLetterEnvelopeQuery { ReceivedAt = $"local://{subscription.ToLowerInvariant()}/", PageSize = 0 },
                cancellationToken
            )
        ).Envelopes;

    // Once the cause is fixed. The store moves each back to its subscription's queue on its next recovery poll.
    public Task ReplayAsync(IEnumerable<DeadLetterEnvelope> deadLetters, CancellationToken cancellationToken) =>
        runtime.Storage.DeadLetters.ReplayAsync(
            new DeadLetterEnvelopeQuery([.. deadLetters.Select(deadLetter => deadLetter.Id)]),
            cancellationToken
        );
}
```

- Replaying marks the dead letter for replay; the store's next recovery poll moves it back to its subscription's queue, with its attempts counted from zero. Only that subscription's handler runs: the other subscriptions of the message are not delivered again. If it fails again, it is retried and set aside again.
- A replay never applies a message twice. A delivery replayed after one of its attempts committed, such as one set aside again by hand, finds its Receipt and does nothing, so replaying the same delivery twice applies it once.
- Fix the cause before replaying: a replay into the same failure only uses up the retries again.

## The message store

- `PouryaKitMessageStoreOptions` (`PouryaKit.Messaging.Wolverine.EFCore`) holds the store's settings:
  - `Durability`: `DurabilityMode.Balanced`, the default, is safe with any number of Host instances on one store. `DurabilityMode.Solo` assumes a single instance and recovers a stopped instance's deliveries at once; the tests use it.
  - `InboxStaleTime`, 5 minutes by default: how long after a delivery was stored the store hands it out again when an instance still holds it. It is what delivers a Notification or Integration Event committed but never sent while its Host lives on, so such a delivery waits that long. Set it by [the stale-time rule](#the-stale-time-rule).
- The store's schema is the second argument of `UsePouryaKitWolverine(connectionName, schema)`, `wolverine` by default.
- Change the settings anywhere in the Host's composition, before it is built, as in [the stale-time rule](#the-stale-time-rule).

## The stale-time rule

`InboxStaleTime` counts from when the delivery was stored, not from its latest attempt, and a retry keeps that time. A delivery waiting for its retry is held by no instance, so the store never hands it out again, however long it waits. But an attempt that runs after the stale time has passed is stale from its start: if it still runs when the store next looks, the store hands it out again, concurrently, and again on every recovery poll until one run finishes. The Receipt keeps the effect to one, because each copy waits on the running attempt's Receipt and then does nothing; but each copy holds a database connection while it waits, and when the attempt it waits on fails, a copy runs the handler itself, so a handler with an effect outside the database can repeat it beyond its retries ([What the Kit guarantees](#what-the-kit-guarantees)).

**The rule a Host must follow:** `InboxStaleTime` exceeds the sum of `RetryDelays` plus the run of every attempt, queue wait included, for the slowest handler of the Host, since both settings apply to every delivery it runs. The defaults keep it: 1 s, 10 s and 1 min are 71 s of waiting, which leaves about 3 min 49 s of the 5 minutes for four attempts, just under a minute each.

When a handler is slower, change the two together, and check the sum again: raise the stale time, shorten the delays, or both. For a handler that may run two minutes:

```csharp
// Four attempts of up to 2 min each, plus 5 s + 30 s + 2 min of waiting: 10 min 35 s, under the 15 min stale time.
builder.Services.Configure<PouryaKitMessagingOptions>(messaging =>
    messaging.RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)]
);
builder.Services.Configure<PouryaKitMessageStoreOptions>(store => store.InboxStaleTime = TimeSpan.FromMinutes(15));
```

A longer stale time has its price: a delivery committed but never sent waits that long on a live Host ([The message store](#the-message-store)).

## Startup checks

- **When the Host is composed**, each declaration throws, naming every problem:
  - `AddPouryaKitNotificationHandlers`, for an assembly with no Notification Handler; a handler that handles several Domain Event types, an abstract or generic one, or is generic itself; a wire name or subscription name that is missing, invalid, reserved or colliding; and the same subscription declared for another Module. Repeating its declaration for the same Module adds nothing.
  - `Publish`, for a type that is not a concrete class or is a Domain Event; a wire name that is missing, invalid or colliding; and a type already published under another wire name or by another Module.
  - `PublishSnapshot`, for everything `Publish` refuses, and for a type already published as an Integration Event or as the Snapshot of another aggregate or with another mapper.
  - `Subscribe`, for a Domain Event; an abstract handler; a subscription name that is missing, invalid, reserved or colliding; and the same subscription declared for another Module. Repeating its declaration for the same Module adds nothing.
- **When the Host starts**, it fails, naming each problem, for a subscription to an Integration Event or Snapshot that no Module of the Host publishes; for an Integration Event this process already sends under another wire name; and for an Integration Event that Wolverine had already named otherwise before the Host declared it, because Wolverine keeps the first name it gives a type and would send it under that name. Wire names are declared all or none.
- **After every hosted service started**, it fails when Notification Handlers, Integration Events or Snapshots are declared but nothing writes them to an Outbox: `UsePouryaKitWolverine()` without a connection name, from `PouryaKit.Messaging.Wolverine`, runs Wolverine with no database, for a Host that declares none of them. It also fails when Host configuration made a subscription's queue one that does not store what it receives, such as `LocalQueue(name).BufferedInMemory()`.
- Not checked: that a Module whose Composition Root handles no command of its own, but declares Notification Handlers or subscriptions, has a Unit of Work (one that handles a command is checked when the Host is composed, [modules.md](modules.md#startup-checks)); and that a Module has the Receipt table and, for a Snapshot, the Snapshot version table ([Receipts](#receipts)). Not checked at startup either, but failing the Operation that commits: a Snapshot of an aggregate whose key the database generates, a Snapshot declaration without its Module in a Host of Modules, and one that covers independent aggregates ([The rules of a mapper and a key](#the-rules-of-a-mapper-and-a-key)).

Testing deliveries: [testing.md](testing.md#deliveries).
