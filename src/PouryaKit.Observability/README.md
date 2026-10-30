![PouryaKit.Observability](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-public/assets-v1/assets/covers/PouryaKit.Observability.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Observability?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Observability)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Observability

An activity and a duration metric for every command and query. Each Operation the dispatcher runs is one activity and one measurement, tagged with its type, its Actor kind and its outcome, and with its Module in a Host of Modules, and the Outbox carries the trace into each delivery, in one Host or across RabbitMQ. It emits through `System.Diagnostics` only: exporting is the Host's job.

## Install

```bash
dotnet add package PouryaKit.Observability --prerelease
```

## When to use it

- You observe the Host's Operations as activities and duration metrics.
- You choose what the Host's exporter listens to.
- You follow one trace from an Operation through the Outbox into its deliveries.
- You assert telemetry in a test with listeners.

It wraps the dispatcher of [`PouryaKit.Cqrs`](https://www.nuget.org/packages/PouryaKit.Cqrs). The [Host recipes](https://pouryakit.vercel.app/docs/capabilities/host-recipes) hold the standard OpenTelemetry setup that exports it.

## Quick start

Register it in the Host, after the Modules:

```csharp
builder.AddPouryaKitModules(modules => modules.Add<InventoryModule>());
builder.Services.AddPouryaKitObservability();
```

A Host's exporter listens to the Kit's source and meter, as the service defaults' OpenTelemetry does:

```csharp
.AddMeter(OperationTelemetry.SourceName, ChatClientSource)
.AddSource(builder.Environment.ApplicationName, OperationTelemetry.SourceName, NpgsqlSource, ChatClientSource)
```

## What you get

- One activity and one duration per Operation, with nothing to change in Feature code.
- One trace from an Operation into every delivery it causes, in its Host or in another.
- No exporter and no OpenTelemetry package in the Kit.
- Nothing allocated per dispatch while nothing listens.

> [!WARNING]
> Every Host in the trace registers it. A Host that never calls it emits nothing from the Kit and its deliveries have no activity, so a receiving Host calls it too, or the trace stops at its door.

## Learn more

- [Observability](https://pouryakit.vercel.app/docs/capabilities/observability)
- [The trace through delivery](https://pouryakit.vercel.app/docs/capabilities/observability#the-trace-through-delivery)
- [Exporting](https://pouryakit.vercel.app/docs/capabilities/observability#exporting)
- [Host recipes](https://pouryakit.vercel.app/docs/guides/host-recipes)
