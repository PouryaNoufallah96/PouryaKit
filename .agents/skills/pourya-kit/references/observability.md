# Observability

`PouryaKit.Observability`: one activity and one duration measurement for every command and query the dispatcher runs, and a trace that continues from an Operation through the Outbox into the deliveries it causes. Namespace `PouryaKit.Observability`. It references `PouryaKit.Cqrs` only and emits through `System.Diagnostics`: the Kit has no OpenTelemetry package, no exporter and no Serilog. Exporting is the Host's job, done with the standard OpenTelemetry or Aspire setup that the project templates (#53) carry.

- [Registering](#registering)
- [What is emitted](#what-is-emitted)
- [The trace through delivery](#the-trace-through-delivery)
- [Exporting](#exporting)
- [Testing telemetry](#testing-telemetry)

## Registering

On the Host, after `AddPouryaKitModules`, which registers the dispatcher this wraps:

```csharp
using PouryaKit.Modularity;
using PouryaKit.Observability;

builder.AddPouryaKitModules(modules =>
{
    modules.Add<CatalogModule>();
    modules.Add<InventoryModule>();
});
builder.Services.AddPouryaKitObservability();
```

- In an application without Modularity, call it after `AddPouryaKitCqrs`. With no dispatcher registered yet it throws `InvalidOperationException`: "No dispatcher of the Kit is registered to observe. Call AddPouryaKitObservability after AddPouryaKitModules or AddPouryaKitCqrs." A second call does nothing.
- A Host that never calls it has no activity and no measurement from the Kit. With it and nothing listening, each dispatch costs one check.
- Feature code changes nothing. A handler starts no activity of its own for its command, and the Kit adds no pipeline step an application could register.

## What is emitted

The names are constants on `OperationTelemetry`:

| What | Name |
|---|---|
| `ActivitySource` and `Meter` | `PouryaKit` (`OperationTelemetry.SourceName`) |
| Activity, one per command or query, kind internal | `pouryakit.operation` (`ActivityName`) |
| Histogram of each one's duration, unit `s`, a `double` that keeps fractions | `pouryakit.operation.duration` (`DurationName`) |
| Tag: the Module the Operation runs in, by type name; absent without Modules | `pouryakit.module` |
| Tag: the command's or query's type name | `pouryakit.operation` |
| Tag: `Person` or `System` | `pouryakit.actor.kind` |
| Tag: `success`, `failure` (a failed Result) or `exception` | `pouryakit.outcome` |

- The activity is current for the whole Operation: its authorization and validation, its handler, the Domain Event drain, the Unit of Work's Outbox write and its commit. A nested command or query is a child activity of the one that sent it.
- The status is `Ok` for a success, `Error` with the Error codes (`Note.Rejected`) for a failed Result, and `Error` with the exception's type for an exception, which is also recorded as an `exception` event. The duration is measured in every case, with the same tags.
- A delivery's outer Operation is tagged `Deliver`, or `RunDelayedCommand` for a delayed or recurring command. The delivered command and the commands its handler sends are its children.

## The trace through delivery

The work an Operation publishes stays in its trace, as a child, never as a link (ADR 0009):

```text
request (ASP.NET Core)                  tracestate=vendor=1
└ pouryakit.operation CreateProductCommand     Catalog
  └ [Wolverine span, only when the Host samples the "Wolverine" source]
    └ pouryakit.operation Deliver               Catalog, publishes ProductCreatedV1
      └ [Wolverine span]
        └ pouryakit.operation Deliver           Inventory    tracestate=vendor=1
```

- Wolverine stores the id of the activity current when the Unit of Work writes the Outbox as the envelope's `parent-id`. The Outbox adds that activity's tracestate as the header `OperationContextHeaders.TraceState` (`pouryakit-tracestate`), which Wolverine does not carry.
- Each delivery restores both into `Operation.TraceParent` when it begins the Operation. The delivery's activity is a child of Wolverine's span of the envelope when the Host samples the `Wolverine` source, and of the publishing Operation when it does not. Either way it carries the publishing tracestate. Without this step every hop would start a new trace.
- An occurrence of a recurring command continues no Operation, so its delivery restores no parent: its Operation's activity is a child of Wolverine's span of the envelope when the Host samples the `Wolverine` source, and the root of a trace of its own when it does not ([messaging.md](messaging.md#recurring-commands)).
- A retry is read back from the message store and stays in the trace, as a sibling of the attempt that failed. A delivery recovered on another Host keeps the parent too (proven in Spike #46).
- This is proven for durable local delivery in one process. Propagation over RabbitMQ between processes is #52's, so do not claim it before that ticket.

## Exporting

The Host adds an exporter for the source and the meter named `PouryaKit`, and for `Wolverine` if it wants Wolverine's spans as well, with the standard OpenTelemetry SDK or Aspire's service defaults. Logs, Serilog included, are configured on the Host too. None of it belongs in a Module or in the Kit.

## Testing telemetry

You need no backend. Listen in the test, start an activity of your own for the request, and filter what you recorded by its trace id, because the Host is shared and earlier deliveries may still be in flight:

```csharp
using System.Diagnostics;
using PouryaKit.Observability;

var stopped = new System.Collections.Concurrent.ConcurrentQueue<Activity>();
using var listener = new ActivityListener
{
    // By name: a static source created while the listener is added would never be offered to it.
    ShouldListenTo = source => source.Name is OperationTelemetry.SourceName or "MyTests",
    Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
    // Wolverine starts its spans with a string parent id; listen to "Wolverine" and sample here to see them.
    SampleUsingParentId = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
    ActivityStopped = stopped.Enqueue,
};
ActivitySource.AddActivityListener(listener);
```

- Assert on tags, `Status`, `ParentSpanId` and `TraceStateString`, not on timings. To assert a duration, use a `MeterListener` for the instrument `pouryakit.operation.duration` of the meter `PouryaKit`: a sub-second Operation has a value above zero with a fraction.
- A test's tracestate reaches a first delivery attempt from the envelope held in memory. Only a retry reads the header back from the store, so test a retry when the header itself is the claim.
- `ObservabilityTests` in the Proving Sample's tests does all of this for the Catalog-to-Inventory chain. Its `RecordedTelemetry` holds the listeners, and `DeliveryProbes.ActivitiesOf(subscription)` gives the activity each delivery attempt ran in.
