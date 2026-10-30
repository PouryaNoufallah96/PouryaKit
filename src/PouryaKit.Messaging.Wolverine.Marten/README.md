![PouryaKit.Messaging.Wolverine.Marten](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-public/assets-v1/assets/covers/PouryaKit.Messaging.Wolverine.Marten.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Messaging.Wolverine.Marten?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Messaging.Wolverine.Marten)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Messaging.Wolverine.Marten

A Stream's messages sent through Wolverine's Marten outbox. It registers the Host's one Marten store and joins it to the Kit's one message store, so a Stream Module's events commit with the Notifications and Integration Events they cause.

## Install

```bash
dotnet add package PouryaKit.Messaging.Wolverine.Marten --prerelease
```

## When to use it

- A Host runs Stream Modules, beside EF Core Modules or without them.
- A Stream publishes Integration Events.

It joins [`PouryaKit.Persistence.Marten`](https://www.nuget.org/packages/PouryaKit.Persistence.Marten) to [`PouryaKit.Messaging.Wolverine`](https://www.nuget.org/packages/PouryaKit.Messaging.Wolverine). In a Host that also has EF Core Modules, [`PouryaKit.Messaging.Wolverine.EFCore`](https://www.nuget.org/packages/PouryaKit.Messaging.Wolverine.EFCore) declares the store this one joins.

## Quick start

Register the Host's Marten store, after the message store's registration when the Host also has EF Core Modules:

```csharp
builder.UsePouryaKitWolverine(connectionName: "app");
builder.UsePouryaKitMarten(connectionName: "app");
```

## What you get

- A Stream's events, Notifications and Integration Events in one commit.
- One store for every Stream Module of the Host, and one message store.
- In a Host of Stream Modules alone, a registration that declares the message store itself.

> [!WARNING]
> Call this registration second in a Host with EF Core Modules: in the other order the EF call throws, naming the order, since Wolverine is registered once.

## Learn more

- [Streams](https://pouryakit.vercel.app/docs/capabilities/stream)
- [Registering a Stream Module](https://pouryakit.vercel.app/docs/capabilities/stream#registering)
- [Stream](https://pouryakit.vercel.app/docs/concepts/stream)
- [What the Kit guarantees](https://pouryakit.vercel.app/docs/capabilities/stream#what-the-kit-guarantees)
