![PouryaKit.Aspire](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-pub/assets-v1/assets/covers/PouryaKit.Aspire.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Aspire?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Aspire)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Aspire

The containers a Kit Host runs on locally, for an Aspire AppHost and nothing else. It adds PostgreSQL, Redis, RabbitMQ and Ollama, each pinned to the image tag the Kit's own tests run, with its data in a named volume that outlives a run, so your local infrastructure is the one the Kit's tests prove.

## Install

```bash
dotnet add package PouryaKit.Aspire --prerelease
```

## When to use it

- You run the application's Hosts and their infrastructure locally with Aspire.
- You give each Host the database, cache and broker its composition uses, under its connection names.
- You run an agent against a local model, or test the AppHost headless.

It references no other Kit package and reaches no other package's closure, so keep it out of your Hosts' own projects. An AppHost also runs the Reverse proxy of [`PouryaKit.ReverseProxy`](https://www.nuget.org/packages/PouryaKit.ReverseProxy) in front of them.

## Quick start

Add the resources the compositions use, then give each Host its own, as the Proving Sample's AppHost does (each Host also takes its bearer settings):

```csharp
var postgres = builder.AddPouryaKitPostgres("postgres");
var cache = builder.AddPouryaKitRedis("cache");
var broker = builder.AddPouryaKitRabbitMq("broker");
var shop = postgres.AddDatabase("modular-monolith-db", "modular_monolith");
var inventory = postgres.AddDatabase("inventory-db", "inventory");

var inventoryHost = builder
    .AddProject<Projects.Inventory_Host>("inventory")
    .WithHttpEndpoint()
    .WithHttpHealthCheck("/alive")
    .WithReference(inventory, "app")
    .WithReference(broker)
    .WaitFor(inventory)
    .WaitFor(broker);
```

## What you get

- Each image pinned to the tag the Kit's tests run.
- A named volume per resource, which outlives a run, so data persists between runs.
- Nothing of Aspire in any other Kit package's closure.

> [!WARNING]
> The AppHost applies no migration. The databases it creates hold no table of an EF Core Module until you apply each Module's migrations to them, and no Host applies one at start.

## Learn more

- [Aspire resources](https://pouryakit.vercel.app/docs/capabilities/aspire)
- [The AppHost](https://pouryakit.vercel.app/docs/capabilities/aspire#the-apphost)
- [What each registration adds](https://pouryakit.vercel.app/docs/capabilities/aspire#what-each-registration-adds)
- [Limits](https://pouryakit.vercel.app/docs/capabilities/aspire#limits)
