# Messaging

`PouryaKit.Messaging`, `PouryaKit.Messaging.Wolverine`, `PouryaKit.Messaging.Wolverine.EFCore`: Notification Handlers that run after the commit, durably, each retried and recorded on its own, on Wolverine with its message store in PostgreSQL. Feature code uses `PouryaKit.Messaging` only. The Composition Root uses `PouryaKit.Messaging.Wolverine`; the Host and the Receipt migration use `PouryaKit.Messaging.Wolverine.EFCore`, which brings the other two, so a Module that declares Notification Handlers references it.

*Not yet available*: Integration Events and their handlers (#16), a second subscriber and dead letters (#17), Snapshots and Tombstones (#18).

- [Notification Handler or Domain Event Handler](#notification-handler-or-domain-event-handler)
- [Writing a Notification Handler](#writing-a-notification-handler)
- [Declaring handlers and running deliveries](#declaring-handlers-and-running-deliveries)
- [What the Kit guarantees](#what-the-kit-guarantees)
- [Receipts](#receipts)
- [Names](#names)
- [Retries](#retries)
- [The message store](#the-message-store)
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
- A Notification Handler runs after the commit, in an Operation of its own. Use it for anything that reaches outside the Module's own data and cannot be taken back, such as sending an email, pushing to a client or calling another system. Its failure is retried; it never rolls back the change, which is already committed.
- One Domain Event may have both, as the sample's `ProductCreatedDomainEvent` does: `CountProductInCategoryHandler` counts the product in the same commit, and `AnnounceNewProductNotificationHandler` announces it afterwards. A Domain Event needs no Domain Event Handler to be delivered as a Notification.

## Writing a Notification Handler

- Implement `INotificationHandler<TDomainEvent>` (`PouryaKit.Messaging`) for one concrete Domain Event type, in an `internal sealed` class of the Business Area that raises it, under `Events/Notifications/`. The handler and its Domain Event stay `internal`. It runs only for a Domain Event of exactly its type, never for one derived from it.
- Return a Result. A failed Result is a failure like an exception: the delivery is retried ([Retries](#retries)).
- Reach outside the Module through a port: an interface the Module declares beside the handler, public so the Host can implement it, as `INewProductAnnouncer` above. The Host registers the implementation (below). The handler never names a transport, an SMTP client or another Module's types.
- Inject what a command handler may: the `OperationContext` (a continuation of the raising Operation), the Module's repositories, `IDispatcher`. A change made through the Module's repositories commits in the handler's own Unit of Work, with its Receipt, and a Domain Event it raises is handled as in any Operation. A change that must commit with the change that raised the Domain Event belongs in a Domain Event Handler instead.
- Never save, commit, begin a transaction, touch the Outbox or reference a Wolverine type: the delivery and its Unit of Work own all of that.

## Declaring handlers and running deliveries

- In each Module's Composition Root, call `module.AddPouryaKitNotificationHandlers(typeof(CatalogModule).Assembly, typeof(CatalogModule))` (`PouryaKit.Messaging.Wolverine`). It finds every Notification Handler in the assembly, internal ones included, subscribes each one to its Domain Event under its subscription name ([Names](#names)), and runs its deliveries in the Module named by the second argument, the Module's definition type, so they reach that Module's Unit of Work. It throws when the assembly has no Notification Handler. An application without Modules calls it on its own services and leaves the Module out.
- That Module needs its own Unit of Work (`UsePostgres`, [persistence.md](persistence.md#postgresql)) and the Receipt table ([Receipts](#receipts)).
- In the Host, call `builder.UsePouryaKitWolverine(connectionName: "app")` (`PouryaKit.Messaging.Wolverine.EFCore`) once; Modules added after it still get their subscriptions. It runs the Host's deliveries on Wolverine with its message store in the database of that connection string, which must be the one the Modules' DbContexts use: the Outbox is written in their transactions. Wolverine creates its tables in the store schema when the Host starts. Wolverine discovers no handlers of its own.
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

## What the Kit guarantees

- **Stored in the commit.** The Unit of Work writes each Domain Event the Operation raised that a Notification Handler subscribes to into the Outbox, once per subscription, after it prepares persistence and before it saves, in the same transaction. The Notification exists if and only if the change commits: a failed Result, a failed Domain Event Handler, a refused save or an exception leaves none. A Domain Event that no Notification Handler subscribes to stores nothing.
- **Sent after the commit.** The Unit of Work sends what it wrote only after the commit. When the send fails, or the Host dies first, the Notification stays in the store: a Host that starts recovers it, and a live Host hands it out again after `InboxStaleTime` ([The message store](#the-message-store)). The Operation's Result is unaffected, because its change is committed; the failed send is logged as a warning.
- **A continuation in its own scope.** Each delivery attempt runs in a new scope, as an Operation of the subscribing Module, begun with a continuation of the raising Operation's context: the same Actor, tenant and correlation, the raising Operation's `OperationId` as its causation, a new `OperationId`. It runs through the dispatcher, with the Module's Unit of Work and the Host's clock, and nothing from a failed attempt reaches the next.
- **Each handler on its own.** Each Notification Handler of a Domain Event has its own stored delivery, retried, recorded and acknowledged apart from the others, so a handler that fails never makes one that succeeded run again.
- **A database effect once.** Before the handler runs, the delivery records a Receipt, the subscription name and the Domain Event's `Id`, in the handler's transaction, so the Receipt commits with the handler's effect or not at all. A redelivery after that commit, such as after a crash before the store marked the delivery handled, finds the Receipt and does nothing. A concurrent copy of the same delivery waits until the first commits, then does nothing; if the first rolled back, the copy runs. A handler whose effect is in its Module's database needs nothing more to be safe to run again.
- **An outside effect at least once.** An effect outside the database, such as an email, cannot roll back with the Receipt. When the attempt that sent it fails or dies before its Receipt commits, the next attempt sends it again. Make a repeat harmless where the outside system allows it, for example by passing it the Domain Event's `Id` as an idempotency key, and do the outside effect last.

## Receipts

- A Module keeps its handlers' Receipts in a table `pouryakit_receipts` in its own schema, keyed by the subscription name, lower-cased, and the Domain Event's `Id`. The table is not part of the Module's model, so a migration creates it: each Module that declares a Notification Handler adds one that calls `CreateReceiptTable` with the Module's schema.
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

Each Notification is stored under its Domain Event's wire name, and each delivery under its handler's subscription name, which is also the name of its queue and half of its Receipt's key. Both are durable identifiers.

- **Wire name, by convention:** the assembly's name and the type's name without the `DomainEvent` suffix, each in kebab case: `catalog.product-created` for `ProductCreatedDomainEvent` in the assembly `Catalog`. It comes from the assembly, not from the Module.
- **Subscription name, by convention:** the wire name, a dot, and the handler's name without the `NotificationHandler` or `Handler` suffix, in kebab case: `catalog.product-created.announce-new-product`.
- **Overrides:** `[WireName("...")]` on the Domain Event and `[SubscriptionName("...")]` on the handler (`PouryaKit.Messaging`) declare a stable name instead.
- **A rename is a breaking change.** A derived name changes when its Domain Event, its handler or its assembly is renamed, or the type moves to another assembly. Deliveries already stored keep the old name: those of a renamed Domain Event no longer find their type, those of a renamed handler arrive on a queue no subscription declares, and either is set aside as a dead letter, never run and never treated as handled. Before renaming a type or handler that uses a derived name while deliveries may be in flight, declare the name it had:

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
- **Case is ignored.** Queues lower-case their names, so two wire names, or two subscription names, that differ only in case are a collision; so are `Shelved` and `ShelvedDomainEvent` in one assembly, whose derived wire names are the same. Changing only the case of a subscription name keeps the same subscription, Receipts included.
- A missing, invalid, reserved or colliding name fails the Host's composition ([Startup checks](#startup-checks)), naming every problem.

## Retries

- A delivery whose handler returns a failed Result or throws is retried after each of `PouryaKitMessagingOptions.RetryDelays` (`PouryaKit.Messaging.Wolverine`; 1 s, 10 s, 1 min and 10 min by default): one attempt more than there are delays, so five. Each wait is stored with the delivery, so a restart neither loses nor repeats it.
- After the last attempt the delivery is set aside as a dead letter in the message store, never marked handled; a failed Result is recorded as a `NotificationHandlerFailedException` naming the subscription and its Errors. The other handlers of the same Domain Event are not affected. Inspecting and replaying dead letters is #17.

## The message store

- `PouryaKitMessageStoreOptions` (`PouryaKit.Messaging.Wolverine.EFCore`) holds the store's settings:
  - `Durability`: `DurabilityMode.Balanced`, the default, is safe with any number of Host instances on one store. `DurabilityMode.Solo` assumes a single instance and recovers a stopped instance's deliveries at once; the tests use it.
  - `InboxStaleTime`, 5 minutes by default: how long a delivery may stay with the instance that took it before the store hands it out again. It is what delivers a Notification committed but never sent while its Host lives on. Set it longer than the longest delivery the Host expects, queue wait included: a delivery still running when it passes is handed out again, and runs again concurrently, on every recovery cycle until one run finishes.
- The store's schema is the second argument of `UsePouryaKitWolverine(connectionName, schema)`, `wolverine` by default.
- Change the settings anywhere in the Host's composition, before it is built:

```csharp
builder.Services.Configure<PouryaKitMessagingOptions>(messaging =>
    messaging.RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15)]
);
builder.Services.Configure<PouryaKitMessageStoreOptions>(store => store.InboxStaleTime = TimeSpan.FromMinutes(15));
```

## Startup checks

- **When the Host is composed**, `AddPouryaKitNotificationHandlers` throws, naming every problem, for an assembly with no Notification Handler; a handler that handles several Domain Event types, an abstract or generic one, or is generic itself; and a wire name or subscription name that is missing, invalid, reserved or colliding.
- **When the Host starts**, after every hosted service started, it fails when Notification Handlers are declared but nothing writes their Notifications to an Outbox: `UsePouryaKitWolverine()` without a connection name, from `PouryaKit.Messaging.Wolverine`, runs Wolverine with no database, for a Host that declares no Notification Handler. It also fails when Host configuration made a subscription's queue one that does not store what it receives, such as `LocalQueue(name).BufferedInMemory()`.
- Not checked: that a subscribing Module has a Unit of Work and a Receipt table ([Receipts](#receipts)).

Testing deliveries: [testing.md](testing.md#deliveries).
