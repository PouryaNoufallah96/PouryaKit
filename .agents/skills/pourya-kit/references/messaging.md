# Messaging

`PouryaKit.Messaging`, `PouryaKit.Messaging.Wolverine`, `PouryaKit.Messaging.Wolverine.EFCore`: Notification Handlers that run after the commit, and the Integration Events they publish for other Modules, each delivery durable, retried and recorded on its own, on Wolverine with its message store in PostgreSQL. Feature code uses `PouryaKit.Messaging` only. The Composition Root uses `PouryaKit.Messaging.Wolverine`; the Host and the Receipt migration use `PouryaKit.Messaging.Wolverine.EFCore`, which brings the other two, so a Module that declares Notification Handlers or subscribes Integration Event Handlers references it. A Contracts project references nothing from the Kit.

*Not yet documented here*: Snapshots and Tombstones (#18).

- [Notification Handler or Domain Event Handler](#notification-handler-or-domain-event-handler)
- [Writing a Notification Handler](#writing-a-notification-handler)
- [Declaring handlers and running deliveries](#declaring-handlers-and-running-deliveries)
- [Integration Events between Modules](#integration-events-between-modules)
- [The Integration Event](#the-integration-event)
- [Declaring what a Module publishes and subscribes](#declaring-what-a-module-publishes-and-subscribes)
- [Publishing from a Notification Handler](#publishing-from-a-notification-handler)
- [Writing an Integration Event Handler](#writing-an-integration-event-handler)
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
- Never save, commit, begin a transaction, touch the Outbox or reference a Wolverine type: the delivery and its Unit of Work own all of that.

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

Across Modules, a Module tells the others what happened with an Integration Event (G.5): use one when another Module reacts to something that happened in this one, or keeps its own copy of this one's data. The receiving Module applies it later, in an Operation of its own, and the publishing Operation never waits for it. A synchronous question through the owning Module's Contracts ([modules.md](modules.md#asking-another-module-a-question)) is the exception, for an Operation that cannot go on without the answer.

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
    }
}

// In Catalog's Composition Root, the publishing side (modules.md):
//     module.Publish<ProductCreatedV1>("catalog.product-created.v1", typeof(CatalogModule));
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
  - a second time with the same type in one Notification Handler's delivery, because both would carry one identity and every receiver would apply only the first;
  - where the Operation may not change state: in a query, an authorizer or a validator, even one the Notification Handler runs.

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

- A Module keeps its handlers' Receipts in a table `pouryakit_receipts` in its own schema, keyed by the subscription name, lower-cased, and the Notification's or Integration Event's identity, the Domain Event's `Id`. The table is not part of the Module's model, so a migration creates it: each Module that declares a Notification Handler or subscribes an Integration Event Handler adds one that calls `CreateReceiptTable` with the Module's schema. Inventory calls it in its first migration, beside its own tables. A Module that publishes Integration Events needs it too: the Outbox records each publication there, under the wire name followed by `#published`, which no subscription name can take.
- A publishing Module's table also holds one row per Domain Event and wire name it published for it, keyed `<wire name>#published`. That row is how the Kit refuses a second Notification Handler publishing the same type for one Domain Event; it is not a Receipt, and no subscription name can equal it.
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

- Nothing checks the table at startup. Without it, every delivery to the Module fails with an `InvalidOperationException` that names `CreateReceiptTable`, and is retried and set aside as a dead letter. `HostFixture` applies every migration before it starts the Host, so a test shows the gap at once.

## Names

Each Notification and Integration Event is stored under its wire name, and each delivery under its handler's subscription name, which is also the name of its queue and half of its Receipt's key. Both are durable identifiers.

- **A Domain Event's wire name, by convention:** the assembly's name and the type's name without the `DomainEvent` suffix, each in kebab case: `catalog.product-created` for `ProductCreatedDomainEvent` in the assembly `Catalog`. It comes from the assembly, not from the Module.
- **A Notification Handler's subscription name, by convention:** the wire name, a dot, and the handler's name without the `NotificationHandler` or `Handler` suffix, in kebab case: `catalog.product-created.announce-new-product`.
- **Overrides:** `[WireName("...")]` on the Domain Event and `[SubscriptionName("...")]` on the Notification Handler (`PouryaKit.Messaging`) declare a stable name instead.
- **An Integration Event's names are declared, never derived:** its wire name is the first argument of `Publish` (`catalog.product-created.v1`), and each Integration Event Handler's subscription name the first argument of `Subscribe` (`inventory.product-created`). Neither depends on a CLR name, so renaming or moving the record or the handler keeps them. Put the version in the wire name, and change it only with a new version of the type.
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

- A delivery fails when its handler throws or returns a failed Result: the two are the same failure, and a failed Result is never acknowledged. Nothing the attempt changed commits, its Receipt included.
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
  - `AddPouryaKitNotificationHandlers`, for an assembly with no Notification Handler; a handler that handles several Domain Event types, an abstract or generic one, or is generic itself; and a wire name or subscription name that is missing, invalid, reserved or colliding.
  - `Publish`, for a Domain Event; a wire name that is missing, invalid or colliding; and a type already published under another wire name or by another Module.
  - `Subscribe`, for a Domain Event; an abstract handler; and a subscription name that is missing, invalid, reserved or colliding.
- **When the Host starts**, it fails, naming each problem, for a subscription to an Integration Event that no Module of the Host publishes; for an Integration Event this process already sends under another wire name; and for an Integration Event that Wolverine had already named otherwise before the Host declared it, because Wolverine keeps the first name it gives a type and would send it under that name. Wire names are declared all or none.
- **After every hosted service started**, it fails when Notification Handlers or Integration Events are declared but nothing writes them to an Outbox: `UsePouryaKitWolverine()` without a connection name, from `PouryaKit.Messaging.Wolverine`, runs Wolverine with no database, for a Host that declares neither. It also fails when Host configuration made a subscription's queue one that does not store what it receives, such as `LocalQueue(name).BufferedInMemory()`.
- Not checked: that a subscribing Module has a Unit of Work and a Receipt table ([Receipts](#receipts)).

Testing deliveries: [testing.md](testing.md#deliveries).
