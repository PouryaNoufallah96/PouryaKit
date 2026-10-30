![PouryaKit.Messaging.Wolverine.EFCore](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-pub/assets-v1/assets/covers/PouryaKit.Messaging.Wolverine.EFCore.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Messaging.Wolverine.EFCore?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Messaging.Wolverine.EFCore)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Messaging.Wolverine.EFCore

Every EF Core Module's Outbox and Receipts on Wolverine. It joins each Module's Unit of Work to the Host's message store, so what an Operation publishes is written in the commit of its change, and it keeps each Module's Receipts and Snapshot versions in the Module's own schema.

## Install

```bash
dotnet add package PouryaKit.Messaging.Wolverine.EFCore --prerelease
```

## When to use it

- A Host with EF Core Modules runs deliveries.
- You add the Receipt or Snapshot version table to a Module.

It joins [`PouryaKit.Messaging.Wolverine`](https://www.nuget.org/packages/PouryaKit.Messaging.Wolverine) to [`PouryaKit.Persistence.EFCore`](https://www.nuget.org/packages/PouryaKit.Persistence.EFCore). A Host that also has Stream Modules adds [`PouryaKit.Messaging.Wolverine.Marten`](https://www.nuget.org/packages/PouryaKit.Messaging.Wolverine.Marten) after it.

## Quick start

Run the Host's deliveries, once, on its message store:

```csharp
builder.UsePouryaKitWolverine(connectionName: "app");
```

Add the Receipt table to each Module that handles a delivery, in a migration:

```csharp
migrationBuilder.CreateReceiptTable("billing");
```

A Module that subscribes to a Snapshot also calls `CreateSnapshotVersionTable` with its schema, in a migration, or its first Snapshot delivery fails.

## What you get

- The Outbox written in the same commit as the change.
- Receipts and Snapshot versions in the Module's own schema, so a repeated delivery changes nothing.
- A Host that fails when it is built if a Module has a delivery and no Unit of Work to run it in.

> [!TIP]
> With Stream Modules in the same Host, call this registration before `UsePouryaKitMarten`: the Marten call joins the store this one declares.

## Learn more

- [The message store](https://pouryakit.vercel.app/docs/capabilities/messaging#the-message-store)
- [Receipts](https://pouryakit.vercel.app/docs/capabilities/messaging#receipts)
- [Messaging](https://pouryakit.vercel.app/docs/capabilities/messaging)
- [Outbox and Notifications](https://pouryakit.vercel.app/docs/concepts/outbox-and-notifications)
