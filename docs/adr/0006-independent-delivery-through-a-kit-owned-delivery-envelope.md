---
status: accepted
---

# Each subscribed handler gets its own Kit-owned delivery envelope

Spike S2 (ticket #5) compared two ways to retry and acknowledge each Integration Event Handler on its own, against
WolverineFx 6.46.0 and PostgreSQL. Both gave independent retry and acknowledgment. The Kit takes **Option B**: the Unit
of Work fans each published Integration Event out into one `HandlerDelivery<T>` envelope per subscription, and one
Kit-owned Wolverine handler per Integration Event type resolves the subscribed handler by its subscription name.
Option A, Wolverine's separated per-handler queues, needs about 13 fewer owned lines. It was rejected because it
cannot deliver to an `internal` handler: the handler shape that plan section D uses and that ADR 0001 relies on for
Module isolation. Under Option A the first delivery to an internal handler is dead-lettered with a compile error.
Nothing observed contradicts ADR 0001–0004 or section G.

## Evidence

The Spike code is in `spikes/s2-independent-delivery/` at commit `d265b241434d09cceaabc3ae29abda6004cb446c`. It is not
merged. It was run on 2026-10-05 with `dotnet test spikes/s2-independent-delivery` (.NET SDK 10.0.401, Microsoft
Testing Platform, xunit.v3 4.0.1) against `postgres:18.6` in Testcontainers 4.15.0, with `track_commit_timestamp=on`.
19 of 19 tests passed. Each test gets a fresh database.

The Host is one container. Catalog publishes `ProductCreatedV1`, a Contracts record with no Kit or transport
reference, through the owned Unit of Work of ADR 0004. Inventory and Billing each subscribe one handler. A fourth
Module, Shipping, is used only in the visibility tests. Every handler is a plain class that implements the Spike's
`IIntegrationEventHandler<T>`, returns a `Result` and has no Wolverine type. Each Module's Composition Root registers
its handler with `messaging.Subscribe<ProductCreatedV1, THandler>("<subscription name>")`. Every handler invocation is
logged to `spike.deliveries` before its scripted outcome.

The two options were run in these configurations:

- **Option A (`SeparatedQueues`).** `MultipleHandlerBehavior.Separated`. One Kit-owned Wolverine handler type per
  subscription, the closed generic `SeparatedHandler<THandler, TMessage>`. Each is registered with
  `Discovery.IncludeType` and pinned with `LocalQueue(subscriptionName).AddStickyHandler`. An Integration Event type
  with a single subscription is also routed with `PublishMessage(type).ToLocalQueue(subscriptionName)`. Inventory's and
  Billing's handlers are `public`, because Option A cannot run them otherwise.
- **Option B (`DeliveryEnvelope`).** The default `MultipleHandlerBehavior`. Subscribed handlers are keyed scoped
  services, keyed by subscription name. Each `HandlerDelivery<T>` type is routed with
  `PublishMessage(type).ToLocalQueue("kit.delivery.<wire name>")`.
- **Shared.** Both use `UseDurableLocalQueues`, `OnException<Exception>().RetryWithCooldown(200 ms, 200 ms).Then.MoveToErrorQueue()`
  (three attempts), Balanced durability with ADR 0004's short timers, `MessageIdentity.IdOnly` unless a test varies
  it, and EF `Eager` mode. A Kit naming strategy gives every Contracts type a wire name: `ProductCreatedV1` is
  `catalog.product-created.v1`, and the delivery envelope is `kit.delivery.catalog.product-created.v1`.

| Point | Deciding test (class.method, all cases) | Observed |
|---|---|---|
| 1. Independent retry | `IndependentRetryTests.Each_handler_is_retried_on_its_own` (A and B × failed Result and exception) | Inventory fails twice and then succeeds, so it is invoked 3 times. Billing is invoked once. |
| 1. The test discriminates (control) | `IndependentRetryTests.Combined_handler_reruns_the_handler_that_already_succeeded` | Option A's adapters with the default `ClassicCombineIntoOneLogicalHandler` produce one envelope row and one logical handler. Billing runs first and is invoked 3 times while Inventory fails twice. |
| 2. No redelivery across a kill | `RestartTests.A_handler_that_succeeded_is_not_run_again_after_the_host_is_killed` (A, B) | A child process publishes. Billing succeeds, its row is `Handled`, and Inventory fails once with a one-minute cooldown. The child is killed with SIGKILL, leaving Inventory's row `Incoming` and owned by node 1. A new Host recovers the row and Inventory succeeds. Billing is still at exactly 1 invocation. |
| 3. Failed Result is a failure | `Each_handler_is_retried_on_its_own` (failed Result cases), `DeadLetterTests` | The adapter throws `HandlerFailedException` on a failed Result. Wolverine retries the delivery and then dead-letters it; a failed Result is never acknowledged. |
| 4. Exhausted retries | `DeadLetterTests.Exhausted_retries_set_the_message_aside_for_that_handler_only` (A, B) | Inventory gets 3 failed invocations, then one row moves to `wolverine.wolverine_dead_letters` with the subscription's identity, the wire name in `message_type`, `exception_type` set to `HandlerFailedException` and `replayable = false`. Inventory has no `Handled` row. Billing's row is `Handled` and Billing ran once. Setting `replayable = true` moves the row back to the incoming table, and only Inventory runs again. |
| 5. Acknowledgment per handler | `AcknowledgmentTests.Each_handler_is_acknowledged_by_its_own_envelope_row` (A, B × `IdOnly`, `IdAndDestination`) | Publishing writes 2 rows with distinct envelope ids, both `Incoming`, in the Unit of Work's commit (same `pg_xact_commit_timestamp` as the business row). Each row becomes `Handled` on its own. In Option A the rows differ by `received_at` (the subscription's queue). In Option B both rows share one queue and the subscription is in the body. `IdAndDestination` changes only the primary key, from `(id)` to `(id, received_at)`. |
| Internal handler, Option A | `HandlerVisibilityTests.Separated_queues_cannot_deliver_to_an_internal_handler` | The Host starts normally. At the first delivery, Shipping's chain fails to compile with `CS0122: 'PrepareShippingOnProductCreated' is inaccessible due to its protection level`, and the delivery is dead-lettered. Inventory and Billing compile separately and are delivered. |
| Internal handler, Option B | `HandlerVisibilityTests.Delivery_envelope_delivers_to_an_internal_handler` | The same internal handler is delivered once. |
| Adding a second subscription | `TopologyTests.A_pending_delivery_is_delivered_after_a_second_subscriber_is_added` (A, B) | Inventory is the only subscription and its delivery is pending when the Host stops gracefully (row `Incoming`, owner 0). The next Host adds Billing, and Inventory's pending delivery is delivered under both options. |
| Same, Option A without the single-subscription route (rejected) | `TopologyTests.Unpinned_separated_queues_dead_letter_a_pending_delivery_when_a_second_subscriber_is_added` | The lone handler's delivery went to `local://catalog.product-created.v1/` despite `AddStickyHandler`. After Billing was added, that queue had no handler, and the delivery was dead-lettered with `NoHandlerForEndpointException`. |
| Option B with a queue per subscription through `EndpointFor` (rejected) | `RestartTests.A_delivery_sent_with_EndpointFor_through_the_outbox_is_not_recovered_after_the_host_is_killed` | `outbox.EndpointFor(name).SendAsync` stores the row as `Outgoing` with owner 0 in the incoming table. After a kill and restart, Inventory was not run again within 15 s and the row stayed `Outgoing`. |

Source reading of `jasperfx/wolverine` at tag `V6.46.0` (commit `1f22835a2b3e32dbf4b75c7a5273622ee74a4929`, read from
the release source archive) explains these results:

- `src/Wolverine/Runtime/Handlers/HandlerChain.cs:130`: sticky separation runs only when a message type has more than
  one handler (`grouping.Count() > 1`). Without a sticky endpoint, line 396 names the queue after the handler type's
  full CLR name. `src/Wolverine/Runtime/Handlers/HandlerGraph.cs:231` throws `NoHandlerForEndpointException` for a
  local queue that has no sticky handler.
- `src/Wolverine/Configuration/HandlerDiscovery.cs:94` and `:406`/`:412`: conventional discovery skips non-public
  types, and `IncludeType` rejects non-public and open generic types. A closed generic over an internal type passes
  this check and fails later, at compile time. The generated code is compiled by `AssemblyGenerator` in the
  `JasperFx.RuntimeCompiler` 5.0.0 NuGet package, read decompiled. It names the assembly
  `AssemblyName ?? Path.GetRandomFileName()`, so no `InternalsVisibleTo` can name it. `InternalsVisibleTo("Internal.Generated")`
  was tried on Inventory and Billing during the Spike and still gave CS0122; that attempt was not kept as a test.
- `src/Wolverine/Runtime/Routing/MessageRoute.cs:158`, `:175` and `:226`: a routed send gets a fresh envelope
  (`AcquireOutgoingEnvelope`), so every local destination gets its own id, and for a local route the send sets
  `Status = Incoming` and the node number. `src/Wolverine/Runtime/DestinationEndpoint.cs:39` builds
  `new Envelope(message, agent)` and sets neither.
  `src/Persistence/Wolverine.RDBMS/Durability/CheckRecoverableIncomingMessagesOperation.cs:34` recovers only rows with
  `status = 'Incoming'` and `owner_id = 0`.
- `src/Persistence/Wolverine.RDBMS/Durability/MoveReplayableErrorMessagesToIncomingOperation.cs`: the durability agent
  copies dead letters with `replayable = true` back to the incoming table under the same id and deletes them.
- `src/Persistence/Wolverine.Postgresql/Schema/IncomingEnvelopeTable.cs:20-29`: `received_at` joins the primary key only
  under `IdAndDestination`. The source comment on `src/Wolverine/DurabilitySettings.cs:41-54` recommends
  `IdAndDestination` when one process receives the same envelope id on several external transport endpoints.
- `src/Wolverine/Util/WolverineMessageNaming.cs:142-152`: naming strategies are a process-global static list, and
  `InsertFirst<T>()` requires `T : new()`. The Kit's wire-name registry must therefore be static as well.
- `src/Wolverine/ErrorHandling/FailureRuleCollection.cs:30`: without a policy, `MaximumAttempts` defaults to 3.

## Comparison

Owned code was counted as the lines in the option's own file (`S2.Kit/OptionA/SeparatedQueues.cs`,
`S2.Kit/OptionB/DeliveryEnvelope.cs`) that are neither blank nor `//` or `///` comments, as each option was decided
(Option A without its 2 control-only lines, Option B without its 2 `EndpointFor`-variant lines). The stand-ins both
options share were the same for both and are not in the count: Result and handler interface 13 lines, registration 19,
wire names 19, Unit of Work 32.

| | Option A: separated queues | Option B: Kit delivery envelope |
|---|---|---|
| Independent retry and acknowledgment observed | Yes, with public handlers | Yes, with public and internal handlers |
| Internal handler | Dead-lettered with CS0122 at the first delivery | Delivered |
| Owned code | 30 lines | 43 lines |
| Feature author writes | The handler class (must be `public`) and one `Subscribe<T, THandler>(name)` call | The handler class (may be `internal`) and the same call |
| Transport-neutral declaration | The subscription name becomes a queue name plus a sticky Wolverine handler type per subscription. A lone subscription needs an explicit route, or its queue changes when a second one is added | The subscription name is a DI key carried in the envelope. One route per Integration Event type. Adding a subscription moves nothing |
| Queues | One durable local queue per subscription | One durable local queue per Integration Event type |
| Envelope rows per published Integration Event | One per subscription | One per subscription |
| Telling rows apart, dead letters included | By `received_at` | Only by reading the body (`body` is Wolverine's binary envelope; the tests read it with `encode(body, 'escape')`) |
| `message_type` | The Contracts wire name | `kit.delivery.<wire name>` |

## The registration and adapter code that worked

Option B, the decision. The Composition Root call is the same for both options:

```csharp
// Inventory's Composition Root: no transport type
messaging.Subscribe<ProductCreatedV1, ReserveStockOnProductCreated>("inventory.product-created");
```

Registration, from `S2.Kit/OptionB/DeliveryEnvelope.cs`, inside `UseWolverine`:

```csharp
foreach (var s in messaging.Subscriptions)
    services.AddKeyedScoped(typeof(IIntegrationEventHandler<>).MakeGenericType(s.MessageType), s.Name, s.HandlerType);

foreach (var messageType in messaging.Subscriptions.Select(s => s.MessageType).Distinct())
{
    var deliveryType = typeof(HandlerDelivery<>).MakeGenericType(messageType);
    var wireName = "kit.delivery." + WireNames.Of(messageType);
    WireNames.Register(deliveryType, wireName);                       // Kit IMessageTypeNaming, InsertFirst'ed
    opts.Discovery.IncludeType(typeof(HandlerDeliveryHandler<>).MakeGenericType(messageType));
    opts.PublishMessage(deliveryType).ToLocalQueue(wireName);         // not EndpointFor: see the rejected variant
}
```

Fan-out, called by the Unit of Work once per published Integration Event, inside its transaction and before save:

```csharp
foreach (var s in messaging.For(message.GetType()))
    await outbox.PublishAsync(Activator.CreateInstance(deliveryType, s.Name, message)!);
```

The envelope and its one Wolverine handler per Integration Event type. The handler type is closed only over the
public Contracts type:

```csharp
public sealed record HandlerDelivery<TMessage>(string Subscription, TMessage Message);

public sealed class HandlerDeliveryHandler<TMessage>(IServiceScopeFactory scopes)
{
    public async Task Handle(HandlerDelivery<TMessage> delivery, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();     // Kit: the Module's scope and Operation Context
        var handler = scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventHandler<TMessage>>(delivery.Subscription);
        var result = await handler.Handle(delivery.Message, ct); // Kit: Unit of Work and Receipt around this call
        if (result.IsFailure) throw new HandlerFailedException(delivery.Subscription, result.Error!);
    }
}
```

Option A, rejected, as far as it got. Registration and adapter:

```csharp
opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
foreach (var s in messaging.Subscriptions)
{
    services.AddScoped(s.HandlerType);
    var adapter = typeof(SeparatedHandler<,>).MakeGenericType(s.HandlerType, s.MessageType);
    opts.Discovery.IncludeType(adapter);
    opts.LocalQueue(s.Name).AddStickyHandler(adapter);
    if (messaging.For(s.MessageType).Count() == 1) opts.PublishMessage(s.MessageType).ToLocalQueue(s.Name);
}
// The Unit of Work publishes with outbox.PublishAsync(message); Wolverine fans out to the sticky queues.

public sealed class SeparatedHandler<THandler, TMessage>(IServiceScopeFactory scopes)
    where THandler : class, IIntegrationEventHandler<TMessage>   // THandler appears in generated code: must be public
{
    public async Task Handle(TMessage message, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<THandler>().Handle(message, ct);
        if (result.IsFailure) throw new HandlerFailedException(typeof(THandler).Name, result.Error!);
    }
}
```

## Where the Receipt attaches

In both options the Receipt belongs inside the Kit adapter, around the handler call. The adapter opens the receiving
Module's scope and its Unit of Work transaction, inserts the Receipt keyed by **(subscription name, the Integration
Event's identity)**, runs the handler, and commits the Receipt and the effect together. The key cannot be the Wolverine
envelope id: that id differs per destination in Option A and per delivery in Option B, as `AcknowledgmentTests` shows
with two distinct ids for one Integration Event. Option B carries the subscription name in the envelope. Option A
would have to map the handler type back to its subscription name.

The Spike built no Receipts. Its adapters use no `[Transactional]` middleware and no DbContext, so Wolverine marks the
envelope `Handled` after the adapter returns, in a separate step from the handler's effect. This follows from the
construction and from ADR 0004's EF-mode test; S2 did not test it again. A crash between the effect's commit and the
`Handled` mark therefore redelivers, and the Receipt is what turns the redelivery into a no-op. ADR 0004's note
stands: Wolverine's inbox mark is not a substitute for the Receipt. What S2 adds is that with one envelope per
subscription, the mark is also per handler for retry and dead-lettering.

## What this refines in ADR 0004

- **Queue naming.** ADR 0004 saw local queues named after the type's full CLR name in lower case. That holds with no
  naming strategy. With the Kit's strategy inserted first, the conventional queue for `ProductCreatedV1` was
  `local://catalog.product-created.v1/` (`TopologyTests.Unpinned_…`).
- **Several handlers on one local queue** (ADR 0004's open note). Under the default `ClassicCombineIntoOneLogicalHandler`
  they become one logical handler on one envelope, and a failure re-runs the handlers that had already succeeded
  (`Combined_handler_reruns_the_handler_that_already_succeeded`).
- **`MessageIdentity`.** Local fan-out produced distinct envelope ids under both settings. `IdAndDestination` changed
  only the primary key. The case it exists for, the same envelope id arriving on several external transport endpoints, was not
  tested. It bears on Option A with RabbitMQ in Wave 2, not on Option B, where every delivery has its own id.
- **New trap: `EndpointFor` through the outbox.** Sending through `IMessageBus.EndpointFor(name)` to a durable local
  queue inside the Unit of Work's transaction writes a row that recovery never picks up.
- **New: attempts are not persisted.** With `RetryWithCooldown`, the `attempts` column stayed 0 after a failed attempt,
  both after the kill and after a graceful stop. Retries happen in memory, so the attempt count starts again after a
  crash.
- **Ownership on stop.** A graceful `StopAsync` left a pending row `Incoming` with owner 0, ready for any node. A kill
  left it owned by the dead node (1) until recovery, as ADR 0004 described.
- **Lazy code generation.** Option A's compile failure surfaced at the first delivery, not at Host start. A startup step
  that forces every handler chain to compile was not found under the names searched in the 6.46.0 source, and none was
  tried. Spec story 71 (startup fails when a subscription has no handler) therefore needs the Kit's own startup check.

## Invariants for permanent tests

| Invariant | Seam | Owner |
|---|---|---|
| One published Integration Event writes one durable envelope per subscription, in the Unit of Work's commit | Gateway | #15 |
| No send to a local queue through `EndpointFor` inside the Unit of Work. Only routed `PublishAsync` writes recoverable rows | Structure (and the kill test below) | #15 |
| A handler that fails does not re-run a handler that already succeeded, across retries and across a kill and restart (Inventory and Billing in the Proving Sample) | Gateway | #16 |
| A handler returning a failed Result is retried and never acknowledged | Gateway | #16 |
| Integration Event Handlers and Notification Handlers may be `internal` and reference no Wolverine type | Structure | #16 |
| Every subscription resolves to a handler at startup, or startup fails (story 71; not shown by the Spike) | Gateway | #16 |
| A redelivered envelope applies its effect once, through the Receipt keyed by (subscription, Integration Event identity) | Gateway | #16 |
| Exhausted retries move that subscription's envelope to dead-letter storage, never `Handled`, and leave the other subscriptions' envelopes alone. A replayed dead letter runs only its own handler | Gateway | #17 |

## Open questions for the owner

1. **Is a public handler, or a codegen step, acceptable?** That is the only thing that would bring Option A back, and
   Option A is smaller. Untested routes are `TypeLoadMode.Static` (generated code compiled into the application assembly
   at build time) together with `InternalsVisibleTo` from every Module to that assembly, or public handler classes with
   an architecture test instead of `internal`. Either adds a build step or weakens ADR 0001's isolation by visibility.
2. **Queue granularity in Option B.** Deliveries share one queue per Integration Event type. A queue per subscription
   would need a Wolverine send that sets local status, and in 6.46.0 `EndpointFor` does not. Whether a failing
   handler's in-memory cooldown slows the other handlers on the shared queue was not measured.
3. **Retry policy for #17.** `RetryWithCooldown` keeps the attempt count in memory, so a poison delivery combined with
   repeated crashes is retried without limit. A persisted retry (`ScheduleRetry`) was not tested.
4. **Dead-letter triage.** In Option B the subscription is visible only in the body. Should #17 copy it to a header, or
   offer a Kit query that reads it?
5. **Notification Handlers.** The Spike exercised Integration Event Handlers only. The fan-out point (the Unit of Work's
   publish) and the adapter are the same for Notification Handlers, by construction. Two Notification Handlers in one
   Module are the same internal-visibility case under Option A.

## Considered Options

- **Option A: `MultipleHandlerBehavior.Separated` with sticky handlers.** Rejected. It cannot deliver to internal
  handlers, and it needs an explicit route for a lone subscription to keep its queue stable.
- **Option A without that route.** Rejected. A pending delivery is dead-lettered when a second subscription is added.
- **Option B with a queue per subscription through `EndpointFor`.** Rejected. Its rows are not recovered after a kill.
- **Option A with a public marker type per Module as the generic argument.** Not built. It would separate retry per
  Module, not per handler, which G.2 requires for Notification Handlers.

## Consequences

- `PouryaKit.Messaging.Wolverine` owns `HandlerDelivery<T>`, one closed `HandlerDeliveryHandler<T>` registered per
  subscribed Integration Event type, the keyed handler registration, and the fan-out the Unit of Work calls.
  `PouryaKit.Messaging` keeps only the transport-neutral `Subscribe`/`Publish` declarations and the handler interfaces.
- Subscription names are durable identifiers: they are DI keys, they travel in persisted envelopes, and they will be
  half of the Receipt key. Renaming one strands its pending deliveries (inferred from the keyed lookup, not tested).
- The Kit's wire-name registry plugs into Wolverine as a process-global `IMessageTypeNaming`.
- Startup validation has to cover subscriptions itself. Wolverine compiles handler chains lazily, and a keyed handler
  that is missing is found only at delivery.
- **Test run and `global.json`.** The worktree that ran the Spike has no `global.json`. `dotnet test` used the main
  checkout's root `global.json`, in a directory above it, which selects the Microsoft.Testing.Platform runner. The same
  root file exists on `feat/wave-1`. The Spike's test project carries `xunit.v3` only, with no VSTest packages. A run
  without that `global.json` was not attempted. The runtime package closure is the same as ADR 0004's, so its licence
  table applies. Test-only additions: xunit.v3 4.0.1 and Testcontainers.PostgreSql 4.15.0.
