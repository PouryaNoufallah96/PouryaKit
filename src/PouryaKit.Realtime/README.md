![PouryaKit.Realtime](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-pub/assets-v1/assets/covers/PouryaKit.Realtime.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Realtime?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Realtime)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Realtime

Server-sent events pushed from Notification Handlers. A handler publishes a plain record on a Channel, and the push is a PostgreSQL notification written on the delivery's own transaction, so every instance of the Host hears it only when that delivery commits. Each instance passes it on to its connected clients of the delivery's tenant that listen on the Channel.

## Install

```bash
dotnet add package PouryaKit.Realtime --prerelease
```

## When to use it

- Browsers should see a change as soon as it commits: a new product, a finished job, a message in an inbox.
- You map a Module's SSE endpoint and decide which Channels an Actor may listen on.

It builds on [`PouryaKit.Web`](https://www.nuget.org/packages/PouryaKit.Web) and on the Unit of Work of [`PouryaKit.Persistence.EFCore`](https://www.nuget.org/packages/PouryaKit.Persistence.EFCore), and its pushes come from the Notification Handlers of [`PouryaKit.Messaging`](https://www.nuget.org/packages/PouryaKit.Messaging).

## Quick start

Register the listener in the Host, over the database the Modules store in:

```csharp
builder.Services.AddPouryaKitRealtime(connectionName: "app");
```

Map the Module's SSE endpoint and the rule that admits an Actor to a Channel:

```csharp
internal sealed class NewProductsRealtime : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapPouryaKitRealtime("/events", (_, channel) => channel == NewProduct.Channel);
}
```

Push from a Notification Handler, as the Proving Sample does:

```csharp
internal sealed class AnnounceNewProductNotificationHandler(INewProductAnnouncer announcer, IRealtimePublisher realtime)
    : INotificationHandler<ProductCreatedDomainEvent>
{
    public async Task<Result> Handle(ProductCreatedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        await announcer.AnnounceAsync(
            new NewProductAnnouncement(domainEvent.ProductId.Value, domainEvent.Name),
            cancellationToken
        );
        return await realtime.PublishAsync(
            NewProduct.Channel,
            new NewProduct(domainEvent.ProductId.Value, domainEvent.Name),
            cancellationToken
        );
    }
}
```

## What you get

- A push that reaches clients only once its delivery commits, so a rollback pushes nothing.
- Every instance of the Host hears it, through the database the Modules already use.
- Pushes held to the delivery's tenant, on Channels the endpoint's rule admits.
- No SignalR and no second channel to the client.

> [!WARNING]
> Delivery to clients is at most once. A client receives a push only while it is connected, and nothing is stored or replayed: when it connects again it reads its state through a query.

> [!TIP]
> Keep each push a small fact or a reference; the client reads the rest.

## Learn more

- [Realtime](https://pouryakit.vercel.app/docs/capabilities/realtime)
- [Pushing from a Notification Handler](https://pouryakit.vercel.app/docs/capabilities/realtime#pushing-from-a-notification-handler)
- [Mapping the SSE endpoint](https://pouryakit.vercel.app/docs/capabilities/realtime#mapping-the-sse-endpoint)
- [Limits](https://pouryakit.vercel.app/docs/capabilities/realtime#limits)
