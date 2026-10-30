![PouryaKit.Messaging.Wolverine](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-pub/assets-v1/assets/covers/PouryaKit.Messaging.Wolverine.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Messaging.Wolverine?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Messaging.Wolverine)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Messaging.Wolverine

Durable deliveries on Wolverine, with the message store in PostgreSQL. A Composition Root declares here what a Module handles, publishes and subscribes to, and which commands it runs later or on a cadence. Every one of these is stored in the Outbox in the commit that causes it and sent only after it, and each delivery runs as an Operation of its own, is retried, and ends as a dead letter you can replay when its attempts run out.

## Install

```bash
dotnet add package PouryaKit.Messaging.Wolverine --prerelease
```

## When to use it

- You declare Notification Handlers, publications, Snapshots and subscriptions in a Composition Root.
- A command must run later, such as a quote's expiry, or on a cadence, such as a nightly reconciliation.
- You set the retries, or find and replay dead letters.

The Host chooses its store package: [`PouryaKit.Messaging.Wolverine.EFCore`](https://www.nuget.org/packages/PouryaKit.Messaging.Wolverine.EFCore) for EF Core Modules and [`PouryaKit.Messaging.Wolverine.Marten`](https://www.nuget.org/packages/PouryaKit.Messaging.Wolverine.Marten) for Stream Modules. [`PouryaKit.Messaging.Wolverine.RabbitMq`](https://www.nuget.org/packages/PouryaKit.Messaging.Wolverine.RabbitMq) adds a broker between Hosts.

## Quick start

Declare a Module's messaging in its Composition Root, from the Proving Sample's Catalog and Billing:

```csharp
module.AddPouryaKitNotificationHandlers();
module.Publish<ProductCreatedV1>("catalog.product-created.v1");
module.Subscribe<ProductCreatedV1, OpenPriceListIntegrationEventHandler>("billing.product-created");
module.AddPouryaKitDelayedCommand<ExpireQuoteCommand>("billing.expire-quote");
```

Schedule a delayed command from a handler; the Unit of Work writes it with the quote, in one commit:

```csharp
internal sealed class OpenQuoteCommandHandler(IRepository<Quote, QuoteId> quotes, IDelayedCommands later, TimeProvider time)
    : CommandHandler<OpenQuoteCommand>
{
    public override async Task<Result> Handle(OpenQuoteCommand command, CancellationToken cancellationToken)
    {
        var quote = Quote.Open(new QuoteId(command.QuoteId), time.GetUtcNow() + command.ValidFor);
        await quotes.AddAsync(quote, cancellationToken);
        later.Schedule(new ExpireQuoteCommand(command.QuoteId), quote.ExpiresOn);
        return Result.Success();
    }
}
```

Declare recurring work under a named system Actor, its cadence from the Host's configuration:

```csharp
module
    .AddPouryaKitRecurringCommand(
        "inventory.reconcile-stock",
        systemActor: "inventory.stock-reconciler",
        occurrence => new ReconcileStockCommand(occurrence)
    )
    .BindConfiguration("Inventory:ReconcileStock");
```

## What you get

- Nothing sent before the commit, and what was committed kept in the message store until it is delivered or set aside.
- A subscription of its own for each handler, retried and set aside on its own.
- A Receipt for each delivery, so its database effect applies once.
- Delayed commands that run as a continuation of the Operation that scheduled them, and recurring commands that run as a named system Actor.
- Dead letters you can find and replay.

> [!WARNING]
> The Kit's own deliveries need no generated code, but a Wolverine handler a Host adds does: outside Development, a Host without that code does not start, so generate it before the Release build. See [Prepare a release](https://pouryakit.vercel.app/docs/guides/prepare-a-release).

## Learn more

- [Messaging](https://pouryakit.vercel.app/docs/capabilities/messaging)
- [Delayed and recurring commands](https://pouryakit.vercel.app/docs/concepts/delayed-and-recurring-commands)
- [Schedule a command](https://pouryakit.vercel.app/docs/guides/schedule-commands)
- [Dead letters](https://pouryakit.vercel.app/docs/capabilities/messaging#dead-letters)
- [The Host's checks](https://pouryakit.vercel.app/docs/capabilities/messaging#the-hosts-checks)
