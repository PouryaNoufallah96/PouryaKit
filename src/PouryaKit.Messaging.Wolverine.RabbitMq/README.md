![PouryaKit.Messaging.Wolverine.RabbitMq](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-public/assets-v1/assets/covers/PouryaKit.Messaging.Wolverine.RabbitMq.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Messaging.Wolverine.RabbitMq?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Messaging.Wolverine.RabbitMq)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Messaging.Wolverine.RabbitMq

Integration Events and Snapshots between Hosts over RabbitMQ. Each published wire name goes to a fanout exchange of its own, and each receiving Host has one durable queue, named by its service name, that fans each message out to its local subscriptions. From there a delivery is exactly what it is inside one Host: its own retries, Receipt and dead letter.

## Install

```bash
dotnet add package PouryaKit.Messaging.Wolverine.RabbitMq --prerelease
```

## When to use it

- A Module runs in a Host of its own and receives another Host's Integration Events and Snapshots.
- You split a Module out of the modular monolith and keep its subscriptions working.
- You choose a Host's service name, or deploy a Host that receives.

Only the Hosts change: Modules, Contracts and Feature code never reference it. It adds the broker to [`PouryaKit.Messaging.Wolverine`](https://www.nuget.org/packages/PouryaKit.Messaging.Wolverine), and the [service template](https://pouryakit.vercel.app/docs/templates/service) generates a Host that uses it.

## Quick start

A Host joins the broker under its service name, after its message store:

```csharp
builder.UsePouryaKitWolverine(connectionName: "app");
builder.UsePouryaKitRabbitMq(BrokerConnection, serviceName);
```

A receiving Host declares each wire name it receives from another Host:

```csharp
builder.Services.Receive<ProductCreatedV1>("catalog.product-created.v1");
builder.Services.Receive<ProductCreatedV2>("catalog.product-created.v2");
builder.Services.ReceiveSnapshot<ProductSnapshotV1>("catalog.product-snapshot.v1");
```

## What you get

- A delivery across Hosts with every guarantee of a delivery inside one Host.
- One fanout exchange per wire name, and one durable queue per kind of Host.
- A body its contract cannot read never handled, and set aside as a dead letter at the Host's queue.
- A Host that fails when it is built if it subscribes to what nothing publishes or declares.

> [!WARNING]
> Start a receiving Host before its first publication. A Host's queue and its bindings are declared when it starts, and the broker drops a message no queue is bound to.

## Learn more

- [RabbitMQ between Hosts](https://pouryakit.vercel.app/docs/capabilities/rabbitmq)
- [In-process or RabbitMQ](https://pouryakit.vercel.app/docs/decision-guides/in-process-or-rabbitmq)
- [Deploying a Host that receives](https://pouryakit.vercel.app/docs/capabilities/rabbitmq#deploying-a-host-that-receives)
- [The service template](https://pouryakit.vercel.app/docs/templates/service)
