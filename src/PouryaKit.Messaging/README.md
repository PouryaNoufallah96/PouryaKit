![PouryaKit.Messaging](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-pub/assets-v1/assets/covers/PouryaKit.Messaging.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Messaging?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Messaging)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Messaging

The contracts Feature code uses to react after a commit and to tell other Modules what happened. A Notification Handler reacts to a Domain Event once the change is committed, an Integration Event Handler applies what another Module published, and a Snapshot mapper turns an aggregate into the copy other Modules keep. This package holds no transport: [`PouryaKit.Messaging.Wolverine`](https://www.nuget.org/packages/PouryaKit.Messaging.Wolverine) delivers them.

## Install

```bash
dotnet add package PouryaKit.Messaging --prerelease
```

## When to use it

- Something must happen after a change commits and could not be taken back: send a mail, push to a browser, call another system.
- Another Module reacts to what happened in this one.
- A Module keeps a copy of another Module's data.
- You give a message a stable name, or add a new version of it.

Feature code references this package alone. The Composition Root and the Host use [`PouryaKit.Messaging.Wolverine`](https://www.nuget.org/packages/PouryaKit.Messaging.Wolverine) and [`PouryaKit.Messaging.Wolverine.EFCore`](https://www.nuget.org/packages/PouryaKit.Messaging.Wolverine.EFCore).

## Quick start

A Notification Handler publishes an Integration Event once the product is committed, as the Proving Sample does:

```csharp
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

Another Module applies it in an Integration Event Handler, writing only the effect:

```csharp
internal sealed class OpenStockItemIntegrationEventHandler(IRepository<StockItem, StockItemId> stockItems, OperationContext context)
    : IIntegrationEventHandler<ProductCreatedV1>
{
    public async Task<Result> Handle(ProductCreatedV1 integrationEvent, CancellationToken cancellationToken)
    {
        await stockItems.AddAsync(StockItem.Open(integrationEvent.ProductId, integrationEvent.OccurredOn, context.Tenant), cancellationToken);
        return Result.Success();
    }
}
```

## What you get

- Three kinds of reaction, kept distinct, each with its own contract.
- Handlers that write only the effect: the Kit records the Receipt, commits and acknowledges.
- A Snapshot mapper that says what other Modules see of an aggregate, and its Tombstone.
- Declared wire and subscription names that stay stable when a class is renamed.

> [!WARNING]
> An outside effect happens at least once. The Receipt makes an effect in the Module's database apply once; an email, a push or a call to another system cannot roll back with it, so do the outside effect last.

## Learn more

- [Messaging](https://pouryakit.vercel.app/docs/capabilities/messaging)
- [Outbox and Notifications](https://pouryakit.vercel.app/docs/concepts/outbox-and-notifications)
- [Integration Events](https://pouryakit.vercel.app/docs/concepts/integration-events)
- [Publish and subscribe](https://pouryakit.vercel.app/docs/guides/publish-and-subscribe)
- [Snapshots](https://pouryakit.vercel.app/docs/concepts/snapshots)
