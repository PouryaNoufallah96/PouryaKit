---
status: accepted
---

# A delivery's Operation joins the publishing Operation's trace as a child; the Kit adds only the Operation activity's placement, tracestate and a parent fallback

Spike #46 (spec #33, "Required Spikes" item 2) ran the Proving Sample's Catalog-to-Inventory chain through the Kit's EF
Outbox and durable local queues against WolverineFx 6.46.0 and PostgreSQL, with `ActivityListener`s. Wolverine already
carries the W3C `traceparent` through the Outbox: when the Unit of Work publishes, it writes the current activity's id
as the envelope's `parent-id`, persists it in the stored envelope, and starts its execution span of the delivery as a
child of it, on the first attempt, on a stored retry and on a delivery another Host recovers. Ticket #47 implements
only three things. First, its Operation activity must be current while the Unit of Work writes the Outbox, because that
activity is what Wolverine writes as the parent. Second, it carries `tracestate`, which Wolverine does not. Third, it
gives the delivery's Operation a parent fallback: Wolverine's span exists only when a Host samples Wolverine's own
`ActivitySource`, and without it the delivery's Operation starts a trace of its own.

The relationship is **parent, not link**. A delivery's Operation activity is in the publishing Operation's trace, and
its parent is either Wolverine's execution span of the envelope, whose parent is the publishing Operation, or the
publishing Operation itself. That meets the acceptance in plan section I.3 ("a delivery's activity is a child of the
sending Operation's trace") and supersedes the wording "delivery activities link to the publishing Operation" in I.4:
nothing on this path creates an `ActivityLink`, and a link to an unrelated trace would not satisfy I.3. Nothing observed
contradicts ADR 0001 to 0008 or section G.

## Evidence

The Spike code is `spikes/w3c-propagation/` at commit `c4cf2870993a8bd5bd22ecac895a869166cd0869`, reachable through tag
`spike/w3c-propagation`. It is not merged. One test project, `spikes/w3c-propagation/W3cPropagation.Tests.csproj`, is
the folder's only entry. The tests were run on 2026-10-07:

```
dotnet build spikes/w3c-propagation
dotnet test --project spikes/w3c-propagation/W3cPropagation.Tests.csproj --timeout 300s --output Detailed
```

.NET SDK 10.0.401, Microsoft Testing Platform, xunit.v3 4.0.1, `postgres:18.6` in Testcontainers 4.15.0. 6 of 6 tests
passed. Each test gets a database of its own and Hosts of its own.

**Configuration.**

- **Host.** The Proving Sample's own composition (`ModularMonolithHost.CreateBuilder`: Catalog, Inventory and Billing,
  `UsePouryaKitWolverine(connectionName: "app")`) referenced by `ProjectReference`, with `UsePouryaKitTestDelivery`
  (Solo durability, 100 ms polling, retry delays 100 ms and 100 ms), served by `TestServer`. Nothing under `src/` or
  `samples/` was changed.
- **The chain.** `CreateProduct` (Catalog) commits the Domain Event `ProductCreatedDomainEvent`, which becomes
  Notifications to `catalog.product-created.publish-product-created` and `catalog.product-created.announce-new-product`,
  and the Snapshot `ProductSnapshotV1` to `inventory.catalog-products`. The Notification delivery Operation of
  `publish-product-created`, in Catalog, publishes the Integration Event `ProductCreatedV1`; Inventory's
  `inventory.product-created` and Billing's `billing.product-created` handle it. The publishing Operation of the
  Integration Event is therefore that Notification delivery Operation, one delivery below `CreateProduct`.
- **Stand-in Operation activity.** Each scope's outermost `IDispatcher` call runs inside an activity
  `pouryakit.operation` from the Spike's source `PouryaKit.Spike` (a scoped decorator replacing the `IDispatcher`
  registration). It wraps the Unit of Work, so it is current while the Unit of Work writes the Outbox, as #47's Operation
  activity must be. It covers both the Gateway's scope and the scope `SubscriptionDelivery` opens for each delivery.
- **Upstream activity.** A `PouryaKit.Spike` activity of kind `Server` is current around the `CreateProduct` Gateway
  call, standing in for an incoming request whose W3C context is already restored, with `TraceStateString` either null
  or `kit=spike-46,other=v1`.
- **Listeners.** One `ActivityListener` per test. `Sample` and `SampleUsingParentId` both return
  `AllDataAndRecorded`, because Wolverine starts its spans with a string parent id. `ShouldListenTo` takes
  `PouryaKit.Spike`, and also `Wolverine` in the configuration "Wolverine's source listened". `Activity.DefaultIdFormat`
  is `W3C`.
- **Observation points.** An `IUnitOfWorkOutbox` decorator records `Activity.Current` when the Unit of Work writes the
  Outbox. An `IReceipts` decorator records `Activity.Current` in the receiving scope immediately before the subscribed
  handler runs; no activity starts between it and the handler. The stored envelope is read from
  `wolverine.wolverine_incoming_envelopes.body` with Wolverine's `EnvelopeSerializer.Deserialize`, and the raw bytes
  are searched for `tracestate`.

| Point | Test (`PropagationTests`), configuration | Observed |
|---|---|---|
| What the Outbox stores | `First_attempt`, all four cases (Wolverine's source listened or not × tracestate null or set) | Every stored envelope's `parent-id` is the W3C id (`00-{trace}-{span}-01`) of the activity current when the Unit of Work wrote it: the Notifications and the Snapshot carry `CreateProduct`'s Operation activity, and `inventory.product-created` carries the publishing Notification delivery Operation's. This holds whether or not Wolverine's source is listened. No stored envelope has a header whose name contains `trace`, and no body contains the bytes `tracestate`; the Kit's headers are only `pouryakit-*`. |
| Connected trace | `First_attempt(true, null)`, `First_attempt(true, "kit=spike-46,other=v1")` | Inventory's delivery Operation activity has the upstream activity's trace id. Its parent is Wolverine's span `catalog.product-created.v1` (source `Wolverine`, kind `Internal`, destination `local://inventory.product-created/`), whose parent is the publishing Notification delivery Operation, whose parent is Wolverine's span `catalog.product-created`, whose parent is `CreateProduct`'s Operation. Neither span has a link. Wolverine also emits a `send` span (kind `Producer`) for each envelope, a child of the publishing Operation. |
| tracestate | `First_attempt(true, "kit=spike-46,other=v1")` | `CreateProduct`'s Operation activity inherits the upstream `tracestate`. Wolverine's spans and every delivery Operation activity have `TraceStateString == null`. |
| Wolverine's source not listened | `First_attempt(false, null)`, `First_attempt(false, "kit=spike-46,other=v1")` | Wolverine starts no span. No activity is current in the receiving scope, so every delivery Operation activity is the root of a new trace (parent `0000000000000000`): the publishing Notification delivery Operation, Inventory's and Billing's. Each hop breaks the trace, although each envelope still carries its publisher's `parent-id`. Nothing leaks from the publishing scope through the local queue hand-off. |
| Restoring the parent explicitly | `First_attempt`, all four cases | `ActivityContext.TryParse(parent-id, tracestate, isRemote: true, out var parent)` on the stored `parent-id` of `publish-product-created`, then `StartActivity("pouryakit.operation", ActivityKind.Internal, parent)`, gives an activity in the upstream trace whose parent is `CreateProduct`'s Operation and whose `TraceStateString` is the given tracestate. |
| Stored retry | `Stored_retry` (Wolverine's source listened, tracestate set) | Inventory's first attempt is failed before its Receipt; the Kit's `ScheduleRetry` writes it back to the store and a second attempt runs. Both attempts' Wolverine spans are children of the publishing Operation, in the upstream trace. The retry's span is tagged `wolverine.message.scheduled=True`. Neither span has a link, and tracestate is null on both. |
| Recovered after a restart | `Recovered_after_restart` (Wolverine's source listened, tracestate set) | Host A holds Inventory's first attempt before its Receipt and is stopped with `StopMode.Quick`. Host B, started on the same database, recovers the stored envelope. Its Inventory delivery Operation is in the upstream trace, and its parent is Wolverine's span on Host B, whose parent is the publishing Operation that ran on Host A. tracestate is null. |

A worked example from the final run, `First_attempt(true, "kit=spike-46,other=v1")`, trace `48874ee9f8e45bc205cca8d6b5251411`:

```
upstream (PouryaKit.Spike, Server)                       0336c2b0589680a1  tracestate=kit=spike-46,other=v1
└ CreateProduct Operation (pouryakit.operation)          982958935d86687b  tracestate=kit=spike-46,other=v1
  └ Wolverine catalog.product-created                    3f9d089bdb027274  tracestate=null
    └ Notification delivery Operation (publishes)        83baa54fa51a87cf  tracestate=null
      │   stored parent-id = 00-48874ee9f8e45bc205cca8d6b5251411-83baa54fa51a87cf-01
      └ Wolverine catalog.product-created.v1             7d3546df51497a39  tracestate=null
        └ Inventory delivery Operation                   7b6ff9ce038d7627  tracestate=null
```

Source reading of `jasperfx/wolverine` at tag `V6.46.0` (commit `1f22835a2b3e32dbf4b75c7a5273622ee74a4929`) explains
these results. These points are inferred from source, not observed:

- **Where `parent-id` is written.** `src/Wolverine/Runtime/MessageBus.cs:315` calls `trackEnvelopeCorrelation(Activity.Current, …)`
  in `PublishAsync`, and `:428` sets `outbound.ParentId = activity?.Id`. The Kit's Outbox publishes through a
  `DbContextOutbox`, which is a `MessageContext`, which is a `MessageBus`, and does not override `PublishAsync`
  (`src/Persistence/Wolverine.EntityFrameworkCore/DbContextOutbox.cs:53`). The value is therefore whatever activity is
  current when the Unit of Work writes the Outbox, not at the flush.
- **How it is stored.** `src/Wolverine/Runtime/Serialization/EnvelopeSerializer.cs:424` writes the key `parent-id`
  (`src/Wolverine/EnvelopeConstants.cs:22`) into the envelope body, and `:115-116` reads it back.
- **How the delivery span is parented.** `src/Wolverine/Runtime/HandlerPipeline.cs:71` starts the execution span with
  `WolverineTracing.StartExecuting(envelope)`, named after the message type. `src/Wolverine/Runtime/WolverineTracing.cs:306-311`
  calls `ActivitySource.StartActivity(spanName, kind, envelope.ParentId)` with a string parent, which can carry no
  tracestate. `WolverineTracing.ActivitySource` is named `Wolverine` (`:282-284`): with no listener sampling it,
  `StartActivity` returns null and nothing is current where the Kit's delivery step runs.
- **No tracestate anywhere on this path.** No file read above writes or reads a tracestate. A code search of the
  repository's default branch (not the tag) finds `tracestate` only in `src/Wolverine/Runtime/Interop/CloudEventsMapper.cs`,
  the CloudEvents interop, which the Kit does not use.
- **Why the stored retry has no link.** `src/Wolverine/ErrorHandling/ScheduledRetryContinuation.cs:28` records the
  failed attempt's activity id in an envelope header (`WolverineTracing.RecordPreviousAttempt`, `WolverineTracing.cs:327-333`),
  and `LinkToPreviousAttempt` (`:346-358`) turns that header into an `ActivityLink` on the next attempt. The header is
  added in memory only: the store's reschedule rewrites `execution_time`, `status`, `attempts` and `owner_id`, not the
  body (`src/Persistence/Wolverine.RDBMS/MessageDatabase.Scheduled.cs:54-55`, `:95-120`). The retry is read back from
  the body, without the header. The link exists only for Wolverine's in-memory retries, and the Kit ships none (ADR
  0006, decided question 1).

## Decision

1. **Relationship: child, not link.** A delivery's Operation activity has the publishing Operation's trace id. Its
   parent is Wolverine's execution span of that envelope when one is current, otherwise the publishing Operation itself.
   The Kit creates no `ActivityLink` between a publishing Operation and a delivery. Retries and recovered deliveries
   keep the same parent. They are siblings under the publishing Operation, not linked to one another.
2. **Wolverine already satisfies, and the Kit relies on it.** Writing `traceparent` (as `parent-id`, from the activity
   current at publish), storing it with the envelope in the Outbox, restoring it on the first attempt, on a stored retry
   and on a recovered delivery, and parenting its own execution span on it. The Kit writes no `traceparent` header of
   its own.
3. **#47 implements only these.**
   - **The Operation activity is current while the Unit of Work writes the Outbox.** Inferred from source, not
     separated by a test: Wolverine reads `Activity.Current` in `PublishAsync` (`src/Wolverine/Runtime/MessageBus.cs:315`
     and `:428` at `V6.46.0`), which `WolverineOutbox.WriteAsync` calls during the commit, before the flush. The Spike's
     stand-in wraps the whole dispatcher call, so it is current at both the write and the flush; no test ends it between
     them. An Operation activity that ends before the Unit of Work writes the Outbox is never the parent.
   - **Carry tracestate.** Seam: `src/PouryaKit.Messaging.Wolverine.EFCore/WolverineOutbox.cs`, `WriteAsync`, beside
     `WithOperationContext`. It writes `Activity.Current?.TraceStateString` into a Kit header, because the stored envelope
     has none. The header name follows the Kit's header naming (`OperationContextHeaders`) and is #47's to choose.
   - **Parent fallback and tracestate restore.** Seam: `src/PouryaKit.Messaging.Wolverine/SubscriptionDelivery.cs`,
     `HandleAsync`, the only place that holds the `Envelope`. It parses `envelope.ParentId` and the Kit's tracestate
     header into an `ActivityContext`, and the delivery's Operation activity starts with it as its parent when no
     activity of that trace is current. In both cases the Operation activity's `TraceStateString` is set from the header.
     How the parsed context reaches #47's pipeline step is #47's design. One option is to record it on the scope's
     `Operation` when `BeginOperation` runs.

   No other propagation behaviour is needed for the local path.

   *Implemented in #47 at `49bc97e`: the header is `OperationContextHeaders.TraceState` (`pouryakit-tracestate`), and the
   parsed context reaches the activity as `Operation.TraceParent`; `ObservabilityTests` pins the invariants below.*

The snippet below is mostly untested design for #47. Only one part ran in the Spike. `PropagationTests.cs:84-92`, in
every `First_attempt` case, parsed a stored `parent-id` together with a tracestate the test supplied directly, using
`ActivityContext.TryParse(parentId, traceState, isRemote: true, out var parent)`, then started
`StartActivity("pouryakit.operation", ActivityKind.Internal, parent)`. The observed result was the publishing trace, the
publishing Operation as parent, and that tracestate. The rest never ran: writing the `pouryakit-tracestate` header,
reading it back from the envelope, the conditional parent and setting `TraceStateString`. The header name is a
placeholder.

```csharp
// UNTESTED DESIGN. Write side, in WolverineOutbox.WriteAsync, per published message. Wolverine itself writes
// Activity.Current.Id as the envelope's parent-id in PublishAsync; only the tracestate needs the Kit.
var options = new DeliveryOptions().WithOperationContext(context);
if (Activity.Current?.TraceStateString is { Length: > 0 } traceState)
{
    options.Headers["pouryakit-tracestate"] = traceState;
}

// Read side, in SubscriptionDelivery.HandleAsync: the publishing Operation's context. Reading the header is untested
// design; the TryParse call is the one that ran (PropagationTests.cs:85), with a stored parent-id.
envelope.Headers.TryGetValue("pouryakit-tracestate", out var state);
ActivityContext.TryParse(envelope.ParentId, state, isRemote: true, out var published);

// UNTESTED DESIGN. Where #47 starts the Operation activity: Wolverine's execution span of this envelope is current only
// when a Host samples the "Wolverine" source. A default parent means "the current activity". Starting an activity with
// a parsed parent ran (PropagationTests.cs:86); this condition did not.
var parent = Activity.Current?.TraceId == published.TraceId ? default : published;
using var activity = source.StartActivity("pouryakit.operation", ActivityKind.Internal, parent);
if (activity is not null && published.TraceState is { } restored)
{
    activity.TraceStateString = restored;
}
```

No package is added. `System.Diagnostics` is in the BCL, and the Spike used only the Kit's own pins (WolverineFx 6.46.0,
MIT; `Microsoft.AspNetCore.TestHost` 10.0.12, MIT, test-only).

## Invariants worth a permanent test

| Invariant | Seam | Owner |
|---|---|---|
| With only the Kit's Operation source listened (not `Wolverine`), Inventory's delivery Operation of `ProductCreatedV1` has the publishing Operation's trace id, and its parent is the publishing Notification delivery Operation's span. Red once: without the fallback it is the root of a new trace (`First_attempt(false, …)` observed exactly that). | Gateway (`ModularMonolith.Tests`, test `ActivityListener`) | #47 |
| With `Wolverine` listened as well, the same delivery is in the same trace and its parent is Wolverine's execution span of that envelope, whose parent is the publishing Operation: no second parent and no new trace. | Gateway | #47 |
| A tracestate current when an Operation publishes arrives on the delivery's Operation activity, asserted on a stored retry or a recovered delivery: a durable local queue hands the first attempt the published in-memory envelope (`tests/ModularMonolith.Tests/IntegrationEventTests.cs:203`, `An_integration_event_handler_that_fails_is_retried_in_new_scopes_and_leaves_neither_its_effect_nor_its_receipt`), so only those attempts read it back from the store. | Gateway | #47 |
| The Operation activity is current while the Unit of Work writes the Outbox: the stored `parent-id` names the publishing Operation's span. | Gateway | #47 |

## Limitations

- **Local delivery only.** Every observation is from durable local queues in one process. The RabbitMQ path, including
  whether `parent-id` and the Kit's tracestate header survive the broker's envelope mapping, was not examined. #52 must
  probe and prove it before cross-process propagation is claimed. *Answered by the #52 amendment below: both survive
  the broker, and the receiving Host's fan-out needs one header to keep the parent.*
- **Sampling.** Every listened source sampled everything. The Spike did not examine head sampling, such as a
  parent-based ratio sampler, or the sampled flag in `parent-id`. It also did not examine a Host that disables
  Wolverine's telemetry per endpoint (`TelemetryEnabled = false`). Like an unlistened source, that leaves no span
  current, and the fallback covers it; this is inferred.
- **Id format.** Observed with `Activity.DefaultIdFormat == W3C`, the .NET default. A Host that switches to
  hierarchical ids would store a `parent-id` that `ActivityContext.TryParse` rejects. This was not tested.
- **Upstream context.** The upstream activity stood in for a request whose W3C headers ASP.NET Core had already
  restored. The HTTP ingress itself was not exercised.

## Considered Options

- **Rely on Wolverine alone and require every Host to listen to `Wolverine`.** Rejected. The Catalog-to-Inventory trace
  would hold only if a template recipe added a source the Kit does not own. `First_attempt(false, …)` shows that every
  hop breaks without it. tracestate would still be lost.
- **Always parent the delivery's Operation on the envelope's `parent-id`, ignoring Wolverine's current span.** Rejected.
  When Wolverine's source is listened, its execution span and the Kit's Operation would be siblings, and Wolverine's
  message tags and retry events would no longer enclose the handler's work. The fallback keeps one shape whenever
  Wolverine's span exists.
- **A link from the delivery to the publishing Operation, as I.4 worded it.** Rejected. Wolverine already parents the
  delivery, and a link from a new root would not meet I.3's connected-trace acceptance.
- **A Kit `traceparent` header beside Wolverine's `parent-id`.** Rejected: it would be a second mechanism for a value
  Wolverine already writes, stores and restores.

## Consequences

- #47 owns the Operation activity's placement (around the Unit of Work), the tracestate header, and the parent fallback
  in `SubscriptionDelivery`. It builds nothing else for local propagation.
- Wolverine's `messaging.conversation_id` tag on its spans is the trace id, because `MessageBus` defaults its
  correlation to `Activity.Current.RootId` (`src/Wolverine/Runtime/MessageBus.cs:28`; inferred). The Operation
  Context's correlation and causation (ADR 0007) stay separate from trace context and are unchanged.
- The wording in I.4, "delivery activities link to the publishing Operation", reads as "are children of the publishing
  Operation's trace" under this record. Plan sections I.4 and I.6 carry an inline note pointing here.

## Amendment (2026-10-07, ticket #52: the trace across RabbitMQ)

The decision above stands: the relationship is parent, not link, and the Kit relies on Wolverine's `parent-id` wherever
Wolverine keeps it. Ticket #52 probed the broker path of ADR 0006's #50 amendment before changing any code, and adds one
header for the one hop where Wolverine loses the parent.

**The probe.** `ModularMonolith.Host` with a broker (Catalog and Billing) and `Inventory.Host`, each a real Host on a
database of its own and joined only by RabbitMQ (`rabbitmq:4.3.6`) in Testcontainers, both in the test process, with
`AddPouryaKitObservability()` on both and the listeners of `RecordedTelemetry`. An `ActivityListener` sees its own
process only, which is why the pair runs in one process; #51's child-process run proves the delivery, not the trace. The
chain is the local proof's: `CreateProduct` inside an upstream activity with tracestate `kit=52,other=v1`, the delivery
Operation of its Notification `catalog.product-created.publish-product-created` publishing `ProductCreatedV1`, and
Inventory's `inventory.product-created` delivery in `Inventory.Host`. Observed at `8b7ab96`, before any change:

| Point | Wolverine's source not listened | Wolverine's source listened |
|---|---|---|
| The broker envelope, stored at `rabbitmq://queue/inventory` | `parent-id` is the publishing Notification delivery Operation's W3C id; every `pouryakit-` header arrived, `pouryakit-tracestate` included | the same |
| Each copy the fan-out stores at `local://{subscription}/` | no `parent-id`; every `pouryakit-` header | `parent-id` is Wolverine's execution span of the broker envelope, the fan-out |
| Inventory's delivery Operation | the root of a new trace, with no tracestate | in the upstream trace: its parent is Wolverine's span of the copy, whose parent is Wolverine's span of the broker envelope, whose parent is the publishing Operation; tracestate `kit=52,other=v1` |

So both values cross the broker: WolverineFx.RabbitMQ maps `Envelope.ParentId` to the AMQP header `parent-id` and
carries every other header. The parent is lost inside the receiving Host. The fan-out (`SubscriptionDelivery.FanOutAsync`,
#51) sends each copy with `EndpointFor(local://{subscription}).SendAsync`, and Wolverine's `DestinationEndpoint.SendAsync`
applies the delivery options and then `TrackEnvelopeCorrelation(envelope, Activity.Current)`, which sets the copy's
`ParentId` to `Activity.Current?.Id` (read from the `V6.46.0` package with ilspycmd; inferred, not separated by a test).
With no listener sampling Wolverine nothing is current there, so the copy names no parent, and the delivery's parse of
an absent `parent-id` drops the tracestate with it. The copies' Wolverine `ConversationId` is each copy's own id and
their Wolverine `TenantId` is null; the Kit reads neither, since tenant and correlation travel in its own headers.

**The addition.**

- **`OperationContextHeaders.TraceParent` (`pouryakit-traceparent`).** `CopiesOf`, the one place the copies' headers are
  set, writes the broker envelope's `parent-id` there when it has one, after the `pouryakit-` headers it copies.
- **The copy's own parent first.** `OperationContextHeaderExtensions.ContinuedOperationContext` parses the envelope's
  `parent-id` when it has one, and this header otherwise, with `pouryakit-tracestate`, into `Operation.TraceParent`, as
  before. `PouryaKit.Observability` is unchanged: a delivery's activity takes `Operation.TraceParent` as its parent only
  when no activity of that trace is current. Messaging.Wolverine starts no activity and does not reference
  Observability.
- **Not the rejected Kit traceparent header.** That option wrote the current activity's id beside Wolverine's
  `parent-id` on every envelope, a second mechanism for what Wolverine writes, stores and restores. This header carries
  Wolverine's own stored value across the one hop where Wolverine replaces it, on the copies only.

**Observed after the addition** (`CrossProcessTracingTests`, both listener modes):

- Without Wolverine's source listened, the parent of Inventory's delivery Operation is the publishing Operation in the
  Catalog Host, as inside one Host.
- With it listened, the copy's own `parent-id` wins, and the chain is two Wolverine spans deep: the publishing
  Operation, Wolverine's span of the broker envelope in `Inventory.Host`, Wolverine's span of the copy, then the
  delivery Operation. Inside one Host there is one. Across the broker, decision 1's "Wolverine's execution span of that
  envelope" is the copy's span.
- In both modes the delivery Operation has the upstream trace id and tracestate, on the first attempt, which runs from
  the copy the fan-out handed over in memory, and on a stored retry, which reads the copy back from the store; the two
  attempts are siblings under the same parent. Nothing creates a link.
- Red once: without the header write, or without the fallback read, the unlistened mode starts a new trace again, while
  the listened mode stays in the trace by design; without `pouryakit-tracestate` among the copied headers both modes
  lose the tracestate; without `AddPouryaKitObservability()` in `Inventory.Host` neither has a delivery activity.

**Limitations.** A copy recovered after its Host restarts reads the header from the stored copy, as the stored retry
does; that path was not run separately. Sampling, id format and the ASP.NET Core ingress stay as the Limitations above
say. The proof Host's exporters are the service defaults recipe of `ModularMonolith.ServiceDefaults`, template content
that adds the `PouryaKit` source and meter and not `Wolverine` (spec #33's template decision; plan I.3, "Health,
resilience, service discovery, Serilog"): the trace holds either way.

*Implemented in #52 at `671a7b3`: `OperationContextHeaders.TraceParent`, written by `SubscriptionDelivery.CopiesOf` and read
by `ContinuedOperationContext`; `CrossProcessTracingTests` pins both listener modes.*
