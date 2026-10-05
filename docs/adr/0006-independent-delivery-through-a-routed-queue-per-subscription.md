---
status: accepted
---

# Each subscription gets its own routed queue and a pre-built Kit handler

Spike S2 (ticket #5) compared ways to retry and acknowledge each Notification Handler and each Integration Event
Handler on its own, against WolverineFx 6.46.0 and PostgreSQL.

The Kit takes **Option C**. Each subscription gets one Wolverine route
(`PublishMessage(type).ToLocalQueue(subscriptionName)`), so a published message becomes one durable envelope per
subscription on that subscription's own queue. Each message type gets one pre-built Kit handler, registered with
`WolverineOptions.AddMessageHandler`, which generates no code. The handler finds the subscription from the queue the
envelope was delivered to, then runs the subscribed handler in a new scope.

Of the options that meet the ticket's rule, Option C has the least owned code: 21 lines. It is also the only tested
shape with no Kit envelope type and no generated code. It delivers to `internal` handlers, and it delivers an
`internal` Domain Event to `internal` Notification Handlers. Options whose Wolverine handler is generated code cannot
do the last of these. Nothing observed contradicts ADR 0001–0004 or section G.

## Evidence

The Spike code is in `spikes/s2-independent-delivery/` at commit `a65f7deb0f18abe42c764257fe437a23b9673ed4`. It is not
merged. An earlier round was committed at `d265b24`. The tests were run on 2026-10-05 with
`dotnet test spikes/s2-independent-delivery` (.NET SDK 10.0.401, Microsoft Testing Platform, xunit.v3 4.0.1) against
`postgres:18.6` in Testcontainers 4.15.0, with `track_commit_timestamp=on`. 39 of 39 tests passed. Each test gets a
fresh database.

The Host is one container with these Modules:

- **Catalog** publishes `ProductCreatedV1`, a Contracts record with no Kit or transport reference, through the owned
  Unit of Work of ADR 0004.
- **Inventory and Billing** each subscribe one Integration Event Handler to `ProductCreatedV1`.
- **Shipping** subscribes an `internal` Integration Event Handler under the mixed-case name `shipping.Product-Created`.
  It is used only in the visibility tests.
- **Catalog's own Notifications.** Catalog raises an `internal` Domain Event, `ProductCreated`. Two `internal`
  Notification Handlers in Catalog subscribe to it.

Every handler is a plain class that implements the Spike's `IIntegrationEventHandler<T>` or `INotificationHandler<T>`,
returns a `Result` and has no Wolverine type. Each Composition Root declares wire names and subscriptions with
`Publish<T>(wireName)`, `Subscribe<T, THandler>(name)` and `SubscribeNotification<T, THandler>(name)`. Every option
delivers through the same shared step, `Subscription.DeliverAsync`, which opens a new scope, runs the handler and
throws `HandlerFailedException` on a failed Result. Every invocation is logged to `spike.deliveries` together with the
id of the scope it ran in. Inventory's and Billing's handlers are `public` only because Option A needs that.

These configurations were run:

- **C, `RoutedQueues` (the decision).** The default `MultipleHandlerBehavior`. One route per subscription and one
  `RoutedHandler` per message type, registered with `AddMessageHandler`. The handler takes the subscription from
  `Envelope.Destination.Host`; Wolverine lower-cases local queue names, so names are compared ignoring case.
- **A, `SeparatedQueues`.** `MultipleHandlerBehavior.Separated`. Each subscription has its own generated handler type,
  the closed generic `SeparatedHandler<THandler, TMessage>`, made sticky to the subscription's queue. A message type with
  a single subscription is also routed explicitly to that queue.
- **A with key types, `SeparatedSlots`.** The same, but the generated handler type is `SlotHandler<TMessage, TSlot>`,
  where `TSlot` is a Kit-owned public key type (`Slot`, `Slot<Slot>`, …) mapped to the subscription name.
- **B, `DeliveryEnvelope`.** The previously recorded option. One `HandlerDelivery<T>` per subscription, published to
  `local://kit.delivery.<wire name>/`, handled by the generated `HandlerDeliveryHandler<T>`.
- **B pre-built, `DeliveryEnvelopePrebuilt`.** B with one pre-built handler per envelope type instead of the generated
  one.
- **Shared settings.** `UseDurableLocalQueues`; `RetryWithCooldown(200 ms, 200 ms).Then.MoveToErrorQueue()`, so three
  attempts, unless a test sets another policy; Balanced durability with ADR 0004's short timers; `MessageIdentity.IdOnly`
  unless a test varies it; EF `Eager` mode. A Kit naming strategy gives `ProductCreatedV1` the wire name
  `catalog.product-created.v1` and the Domain Event `ProductCreated` the wire name `catalog.product-created`.

| Point | Deciding test (class.method) and cases | Observed |
|---|---|---|
| 1. Independent retry | `IndependentRetryTests.Each_handler_is_retried_on_its_own` (C, A, B × failed Result and exception) | Inventory fails twice and then succeeds, so it is invoked 3 times. Billing is invoked once. |
| 1. The test discriminates (control) | `IndependentRetryTests.Combined_handler_reruns_the_handler_that_already_succeeded` | With Option A's adapters under the default `ClassicCombineIntoOneLogicalHandler`, there is one envelope row and one logical handler, and Billing is invoked 3 times. |
| 1. Notification Handlers | `NotificationTests.Two_notification_handlers_of_an_internal_domain_event_are_retried_independently` (C, B pre-built) | The internal `ProductCreated` is delivered to both internal handlers. Index runs 3 times and Announce once. There is one `Handled` row per subscription and no dead letter. |
| 1. Generated code and an internal Domain Event | `NotificationTests.Generated_adapters_cannot_deliver_an_internal_domain_event` (B, A with key types) | No handler runs. Both Notifications are dead-lettered, at least one with `CS0122: 'ProductCreated' is inaccessible`. Under B the second dead letter sometimes carries a code-generation error instead ("Frame chain is being re-arranged"), because both deliveries compile the same chain concurrently. |
| 2. No repeat after a kill | `RestartTests.A_handler_that_succeeded_is_not_run_again_after_the_host_is_killed` (C, A, B) | A child process publishes. Billing succeeds and its row is `Handled`. Inventory fails once and waits in a one-minute cooldown. The child is killed with SIGKILL. Inventory's row stays `Incoming` with a non-zero owner (the logged runs show 1). A new Host recovers the row and Inventory succeeds. Billing is still at exactly 1 invocation. |
| 3. Failed Result is a failure | `Each_handler_is_retried_on_its_own` (failed-Result cases), `DeadLetterTests` | Wolverine retries the delivery and then dead-letters it; a failed Result is never acknowledged. |
| 4. Exhausted retries | `DeadLetterTests.Exhausted_retries_set_the_message_aside_for_that_handler_only` (C, A, B) | Inventory fails 3 times, then its row moves to `wolverine.wolverine_dead_letters`. The row has the wire name in `message_type`, `HandlerFailedException` as its exception, and `replayable = false`. Inventory has no `Handled` row. Billing's row is `Handled`. Setting `replayable = true` moves the dead letter back, and only Inventory runs again. |
| 5. Acknowledgment per handler | `AcknowledgmentTests.Each_handler_is_acknowledged_by_its_own_envelope_row` (C, A, B × `IdOnly`, `IdAndDestination`) | Publishing writes 2 rows with distinct envelope ids in the Unit of Work's commit (same `pg_xact_commit_timestamp` as the business row). Each row becomes `Handled` on its own. Under C and A the rows differ by `received_at`, which is the subscription's queue. Under B both rows share one queue and only the body names the subscription. `IdAndDestination` changes only the primary key, from `(id)` to `(id, received_at)`. |
| Per-retry scope | `IndependentRetryTests.Each_attempt_runs_in_a_new_scope` (C, A, B) | 3 attempts ran in 3 distinct scopes. |
| Internal Integration Event Handler | `HandlerVisibilityTests.Delivers_to_an_internal_handler` (C, B, A with key types); `HandlerVisibilityTests.Separated_queues_cannot_deliver_to_an_internal_handler` (A) | C, B and A with key types deliver to Shipping's internal handler. C does so through the lower-cased queue `local://shipping.product-created/`. Under A, Shipping's chain fails to compile (`CS0122` on the handler type) at the first delivery, not at Host start, and the delivery is dead-lettered. The other chains still deliver. |
| Adding a second subscription | `TopologyTests.A_pending_delivery_is_delivered_after_a_second_subscriber_is_added` (C, A, B) | Inventory is the only subscription, and its delivery is pending when the Host stops gracefully (row `Incoming`, owner 0). The next Host adds Billing, and Inventory's delivery is delivered. C needs no special case for this; A needs its explicit route for a single subscription. |
| Same, A without the single-subscription route (rejected) | `TopologyTests.Unpinned_separated_queues_dead_letter_a_pending_delivery_when_a_second_subscriber_is_added` | The lone handler's delivery goes to `local://catalog.product-created.v1/`. After Billing is added, that queue has no handler, and the delivery is dead-lettered with `NoHandlerForEndpointException`. |
| B through `EndpointFor` (rejected) | `RestartTests.A_delivery_sent_with_EndpointFor_through_the_outbox_is_not_recovered_after_the_host_is_killed` | The row is stored as `Outgoing` with owner 0. After a kill and restart it stays `Outgoing` and Inventory does not run again within 15 s. |
| C over a non-local transport | `ExternalTransportTests.Each_handler_is_retried_on_its_own_when_deliveries_arrive_over_tcp` | One receiving TCP endpoint per subscription (Wolverine's built-in transport, durable inbox and outbox), standing in for a broker queue per subscription. Retry is independent (Inventory 3 invocations, Billing 1). The inbox rows have `received_at` `tcp://localhost:<port>/` and the Contracts wire name in `message_type`. The handler finds the subscription by port, because the destination's host is `localhost` and not the subscription name. |
| `InboxStaleTime` with an in-memory retry | `StaleRetryTests.An_in_memory_retry_longer_than_inbox_stale_time_runs_the_handler_again_after_it_succeeded` (C) | `RetryWithCooldown(10 s)` with `InboxStaleTime` 3 s and 1 s polls. Inventory fails, is handed out again and succeeds within 9 s, then runs a third time from the first hand-out's in-memory retry: `[FailedResult, Succeeded, Succeeded]`. |
| Remedy: a persisted retry | `StaleRetryTests.A_persisted_retry_is_not_handed_out_again_by_inbox_stale_time` (C) | `ScheduleRetry(10 s)`. After the failure the row is `Scheduled` with owner 0 and attempts 1. Nothing runs during the next 6 s. Inventory then runs once more: `[FailedResult, Succeeded]`. |
| Remedy: a short in-memory ladder | `StaleRetryTests.An_in_memory_retry_ladder_that_ends_before_inbox_stale_time_runs_the_handler_once_more` (C) | `RetryWithCooldown(1 s)` with a 3 s stale time gives `[FailedResult, Succeeded]` and nothing more within 6 s. |

Source reading of `jasperfx/wolverine` at tag `V6.46.0` (commit `1f22835a2b3e32dbf4b75c7a5273622ee74a4929`, read from
the release source archive) explains these results:

- **`AddMessageHandler`.** `src/Wolverine/WolverineOptions.cs:477-492` documents it as "for advanced usages … pre-canned
  message handling that does not require any code generation". It registers the message type and gives a
  `MessageHandler` its own `HandlerChain` "for error handling and other policies" (also
  `src/Wolverine/Runtime/Handlers/HandlerGraph.cs:99-109`). That is why `OnException` policies applied to C. Generated
  middleware does not run for such a handler; C uses none.
- **Local queue names.** `src/Wolverine/Transports/Local/LocalTransport.cs:117` and
  `src/Wolverine/Transports/Local/LocalQueue.cs:14` lower-case them.
- **One envelope per route.** `src/Wolverine/Runtime/Routing/MessageRoute.cs:158`, `:175` and `:226`: each route gets
  a fresh envelope with its own id, and a local route sets `Status = Incoming` and the node number.
  `src/Wolverine/Runtime/DestinationEndpoint.cs:39` (`EndpointFor`) sets neither.
  `src/Persistence/Wolverine.RDBMS/Durability/CheckRecoverableIncomingMessagesOperation.cs:34` recovers only `Incoming`
  rows with owner 0.
- **Separated mode.** `src/Wolverine/Runtime/Handlers/HandlerChain.cs:130` separates handlers only when a message type
  has more than one. `src/Wolverine/Runtime/Handlers/HandlerGraph.cs:231` throws `NoHandlerForEndpointException`.
  `src/Wolverine/Configuration/HandlerDiscovery.cs:94` and `:406`/`:412` accept only public types. Generated code is
  compiled by `AssemblyGenerator` in the `JasperFx.RuntimeCompiler` 5.0.0 NuGet package (read decompiled) into an
  assembly named `AssemblyName ?? Path.GetRandomFileName()`, so every type named in generated code must be public,
  including generic arguments and message types.
- **Stale release.** `src/Persistence/Wolverine.RDBMS/Durability/BumpStaleIncomingEnvelopesOperation.cs:27` releases
  rows with `owner_id != 0` and an insert `timestamp` older than the stale time, and nothing updates `timestamp` (ADR
  0004).
- **Persisted retry.** `src/Persistence/Wolverine.RDBMS/MessageDatabase.Scheduled.cs:95-99`: `ScheduleRetry` writes
  the row back as `Scheduled`, owner 0, with its attempt count.
- **Other sources.** `src/Persistence/Wolverine.RDBMS/Durability/MoveReplayableErrorMessagesToIncomingOperation.cs`
  (replay). `src/Persistence/Wolverine.Postgresql/Schema/IncomingEnvelopeTable.cs:20-29` (primary key).
  `src/Wolverine/DurabilitySettings.cs:41-54` (`MessageIdentity`). `src/Wolverine/Util/WolverineMessageNaming.cs:142-152`
  (process-global naming strategies; `InsertFirst<T>()` requires `T : new()`).

## Comparison

**Counting rule.** An option's owned code is the lines of its own file that are neither blank nor `//` or `///`
comments, in the configuration the option is judged on, with lines used only by a variant or a control removed. The
stand-ins every option shares are excluded because they are identical for all of them:

| Shared stand-in | Lines |
|---|---|
| Result, the two handler interfaces, the exception | 17 |
| Registration and `Subscription.DeliverAsync` | 47 |
| Wire names | 19 |
| Unit of Work | 32 |

Each option was judged on the code in its file at the Spike commit, with these lines removed:

| Option | File | Removed before counting |
|---|---|---|
| A | `OptionA/SeparatedQueues.cs` | The Classic control ternary |
| A with key types | `OptionA/SeparatedSlots.cs` | Nothing |
| B | `OptionB/DeliveryEnvelope.cs` | The `EndpointFor` and pre-built branches |
| B pre-built | `OptionB/DeliveryEnvelope.cs` | The `EndpointFor` branch and the generated handler |
| C | `OptionC/RoutedQueues.cs` | The TCP branch |

| | C: routed queues | A: separated, handler type | A: separated, key types | B: envelope | B: envelope, pre-built |
|---|---|---|---|---|---|
| Owned lines | **21** | 24 | 31 | 34 | 48 |
| Independent retry and acknowledgment (Integration Event Handlers) | Yes | Yes, public handlers only | Not run (only delivery was tested) | Yes | Not run |
| Internal Integration Event Handler | Delivered | Dead-lettered (CS0122) | Delivered | Delivered | Not run |
| Internal Domain Event to internal Notification Handlers | Delivered, independent retry | Not run (its generated code names both internal types) | Dead-lettered (CS0122) | Dead-lettered (CS0122) | Delivered, independent retry |
| Application handler written against Wolverine | No | No | No | No | No |
| Generated code | None | One chain per subscription | One chain per subscription | One chain per message type | None |
| Queues | One per subscription | One per subscription | One per subscription | One per message type | One per message type |
| Envelope rows per published message | One per subscription | One per subscription | One per subscription | One per subscription | One per subscription |
| Subscription visible in | `received_at` | `received_at` | `received_at` | Body only | Body only |
| `message_type` | Contracts wire name | Contracts wire name | Contracts wire name | `kit.delivery.<wire name>` | `kit.delivery.<wire name>` |
| A single subscription, then a second | No special case | Needs an explicit route | Needs an explicit route | No special case | No special case |
| Wolverine handler type (by construction) | `RoutedHandler`, one per message type | `SeparatedHandler<THandler, TMessage>` | `SlotHandler<TMessage, Slot<Slot<…>>>` | `HandlerDeliveryHandler<T>` | `PrebuiltDeliveryHandler` |

The Feature author writes the same thing under every option: the handler class and one `Subscribe` or
`SubscribeNotification` call in the Composition Root. Option A alone also requires the class to be `public`.

## The registration and handler code that worked

Composition Roots. The Notification wire name and subscription names are declared explicitly; see "Requirements for
#15" below.

```csharp
messaging.Subscribe<ProductCreatedV1, ReserveStockOnProductCreated>("inventory.product-created");   // Inventory
messaging.Publish<ProductCreated>("catalog.product-created");                                         // Catalog, internal Domain Event
messaging.SubscribeNotification<ProductCreated, IndexProductOnProductCreated>("catalog.product-created.index");
```

Option C, from `S2.Kit/OptionC/RoutedQueues.cs`, inside `UseWolverine`:

```csharp
foreach (var s in messaging.Subscriptions) opts.PublishMessage(s.MessageType).ToLocalQueue(s.Name);
foreach (var messageType in messaging.Subscriptions.Select(s => s.MessageType).Distinct())
    opts.AddMessageHandler(messageType, new RoutedHandler(messaging));
// The Unit of Work publishes with outbox.PublishAsync(message); Wolverine writes one envelope per route.

public sealed class RoutedHandler(KitMessaging messaging) : MessageHandler
{
    public override Task HandleAsync(MessageContext context, CancellationToken ct) =>
        messaging.Named(context.Envelope!.Destination!.Host)            // local://{subscription, lower-cased}/
            .DeliverAsync(context.Runtime.Services.GetRequiredService<IServiceScopeFactory>(), context.Envelope.Message!, ct);
}
```

The delivery step every option shares, from `S2.Kit/KitMessaging.cs`. The invoker is built by the generic
`Subscribe<TMessage, THandler>` call inside the Module, so the Kit never names an internal type itself:

```csharp
public async Task DeliverAsync(IServiceScopeFactory scopes, object message, CancellationToken ct)
{
    await using var scope = scopes.CreateAsyncScope();          // Kit: the Module's scope, Unit of Work and Receipt
    var result = await Invoke(scope.ServiceProvider, message, ct);
    if (result.IsFailure) throw new HandlerFailedException(Name, result.Error!);
}
```

For a transport other than local queues, the handler must map the endpoint to the subscription by the endpoint
itself. In the TCP variant that is the port: `ports[destination.Port]`.

## Where the Receipt attaches

The Receipt belongs inside `DeliverAsync`, around the handler call. `DeliverAsync` opens the receiving Module's scope
and its Unit of Work transaction, inserts the Receipt keyed by **(subscription name, the message's identity)**, runs the
handler, and commits the Receipt and the effect together. The message's identity is the Domain Event's id, which an
Integration Event carries (G.2).

The key cannot be the Wolverine envelope id. That id differs per route, as `AcknowledgmentTests` shows with two distinct
ids for one message. Under Option C the subscription name comes from the queue the envelope was delivered to.

The Spike built no Receipts. The pre-built handler runs no Wolverine middleware, so Wolverine marks the envelope
`Handled` after `HandleAsync` returns, separately from the effect. That follows from the construction and from ADR
0004's EF-mode test; S2 did not test it again. A crash in between therefore redelivers, and the Receipt makes the
redelivery a no-op. Concurrent duplicate hand-outs, from `InboxStaleTime` as in ADR 0004 and as below, need the same
Receipt.

## Requirements for #15: naming Notifications

Plan section D declares neither a wire name for a Domain Event delivered as a Notification nor a durable subscription
name for a Notification Handler. Both are needed:

- The Domain Event's wire name goes into `message_type` and resolves the type when the envelope is read back.
  `WireNames.Of` throws for a type that has none.
- The subscription name is the queue name and the Receipt's key.

The Spike declared both explicitly in Catalog's Composition Root. #15 has to choose between explicit declaration and a
convention such as `<module>.<event>` and `<module>.<event>.<handler>`. Startup must fail when a published Notification
has no wire name or a subscribed handler has no subscription (spec story 71).

## What this refines in ADR 0004

- **How Notifications are distinguished.** ADR 0004's consequence says "distinguished by CLR type and local queue".
  Under this decision Notifications and Integration Events are distinguished by their wire name (`message_type`) and by
  the subscription's queue (`received_at`). No CLR name is stored in those columns. The body was not checked.
- **Queue naming.** The local queue is the subscription name, lower-cased by Wolverine, not the type's full CLR name.
  Subscription names are compared ignoring case (`Delivers_to_an_internal_handler`, C).
- **Several handlers on one local queue** (ADR 0004's open note). Under Classic they become one logical handler on one
  envelope, and a failure re-runs the handlers that had already succeeded (the control test).
- **Per-retry scope** (plan section F; ADR 0004 left it to S2). Each attempt runs in a new scope that the Kit opens in
  `DeliverAsync` (`Each_attempt_runs_in_a_new_scope`).
- **`MessageIdentity`.** Routed fan-out gives each route its own envelope id, locally and over TCP. `IdAndDestination`
  changed only the primary key and nothing else that was observed. The case it exists for, one envelope id arriving on
  several endpoints, was not tested; see Consequences on brokers.
- **`EndpointFor` through the outbox** writes a local row as `Outgoing`, owner 0, and recovery never picks it up.
- **`InboxStaleTime` against in-memory retries.** With ADR 0004's remedy set, a delivery waiting in an in-memory
  `RetryWithCooldown` is an owned row like any other. Once its insert time is older than the stale time it is released
  and handed out again, and later the original in-memory retry runs as well. The handler then runs again after it has
  already succeeded (`StaleRetryTests`, first test). Two remedies held:
  - **A persisted retry (`ScheduleRetry`).** The row is written back as `Scheduled` with owner 0, which the release
    ignores, and the attempt count is persisted with it.
  - **A short in-memory ladder.** Because the release compares the insert time, which nothing updates, the whole ladder
    plus handling must end before the stale time, not just each step. That is inferred from source; only a one-step
    ladder was tested.

  Not tested: after a scheduled retry fires, the row is owned again with its old insert time. A run longer than one
  recovery poll could then be handed out concurrently; that is inferred from the same SQL.
- **Attempt count.** With `RetryWithCooldown` the `attempts` column stayed 0 after a failure, after a kill and after a
  graceful stop, so in-memory retries start counting again after a crash. `ScheduleRetry` persisted 1.
- **Lazy code generation.** Generated chains compile at first delivery, not at Host start. Option C generates none, but
  spec story 71 still needs the Kit's own startup check of every subscription.

## Invariants for permanent tests

| Invariant | Seam | Owner |
|---|---|---|
| One published Notification or Integration Event writes one durable envelope per subscription in the Unit of Work's commit, each on its subscription's queue | Gateway | #15 |
| No local send through `EndpointFor` inside the Unit of Work | Structure | #15 |
| Every published Notification type has a wire name and every Notification Handler a subscription name, or startup fails | Gateway | #15 |
| An `internal` Domain Event reaches `internal` Notification Handlers; Notification Handlers and Integration Event Handlers may be `internal` and reference no Wolverine type | Gateway and Structure | #15, #16 |
| A failing handler never re-runs one that succeeded: across retries, across a kill and restart, and between two Notification Handlers of one Domain Event (Inventory and Billing in the Proving Sample) | Gateway | #16 |
| A failed Result is retried and never acknowledged; each attempt runs in a new scope | Gateway | #16 |
| Every subscription resolves to a handler at startup, or startup fails (story 71) | Gateway | #16 |
| A redelivered or concurrently duplicated envelope applies its effect once, through the Receipt keyed by (subscription, message identity) | Gateway | #16 |
| Exhausted retries move only that subscription's envelope to dead-letter storage, never marked `Handled`. A replay runs only that handler. The dead letter is found by `received_at` | Gateway | #17 |
| With `InboxStaleTime` set, a delivery waiting to retry is not handed out again and does not run again after it succeeded. Use a persisted retry, or keep the whole retry ladder plus handling below the stale time | Gateway | #17 |

## Open questions for the owner

None of these reverses the decision.

1. **Retry policy (#17).** With `InboxStaleTime` on (ADR 0004's remedy for #15), the evidence favours a persisted retry
   (`ScheduleRetry`). A persisted retry also keeps the attempt count across a crash. An in-memory ladder holds only while
   the whole ladder plus handling stays below the stale time. Which one should the Kit default to?
2. **Naming Notifications (#15).** Should the wire name and the subscription name be explicit declarations or a
   convention? See "Requirements for #15" above.
3. **Subscription-name rules (#16).** Subscription names become local queue names: Wolverine lower-cases them, and C
   reads them back from the queue URI's host. The Kit should reject names that differ only in case (the Spike's
   registration does) and names that are not valid as a URI host (not tested).

The earlier question, whether a public handler or a code-generation build step is acceptable, is dissolved: Option C
needs neither. The dead-letter triage question is dissolved too: the subscription is in `received_at`.

## Considered Options

- **A, separated queues with the handler type in generated code.** Rejected. It cannot deliver to an internal handler
  or an internal Domain Event, and a single subscription needs an explicit route.
- **A with key types.** Rejected. It cannot deliver an internal Domain Event, because the message type is still in
  generated code. It is 31 lines, it needs the single-subscription route, and Wolverine's logs name nested `Slot<…>`
  types.
- **B, a Kit delivery envelope with a generated handler.** Rejected. It cannot deliver an internal Domain Event (R2 of
  the review). It uses one queue per message type, so the subscription is visible only in the body.
- **B with a pre-built handler.** It works, but it is rejected: it is Option C's mechanism plus a Kit envelope type, at
  48 lines.
- **B through `EndpointFor`, and A without the single-subscription route.** Rejected for the observed failures above.

## Consequences

- `PouryaKit.Messaging.Wolverine` owns the route per subscription and one pre-built `MessageHandler` per subscribed
  message type, together with the shared delivery step (scope, Unit of Work, Receipt, failed Result to exception).
  `PouryaKit.Messaging` keeps the transport-neutral `Publish`, `Subscribe` and `SubscribeNotification` declarations and
  the handler interfaces.
- The Kit relies on `AddMessageHandler`, which Wolverine labels "for advanced usages". No Wolverine middleware runs for
  these handlers. Wolverine's failure policies do run, as the tests show. The Gateway tests in the invariants table are
  what will catch a change in a later Wolverine version.
- Subscription names are durable identifiers: they are queue names, they appear in `received_at` of stored envelopes,
  and they are half of the Receipt key. Renaming one strands its pending envelopes on the old queue (inferred, not
  tested).
- **Brokers in Wave 2.**
  - The bare Notification or Integration Event is what is stored and sent, under its own wire name. The TCP test shows
    it arriving with the Contracts wire name in `message_type`.
  - A broker queue per subscription keeps this shape: one route per subscription, and the Kit maps the receiving
    endpoint to its subscription by that endpoint. The map is the only Kit-owned addition. It cannot be the URI host,
    which for TCP was `localhost`; a broker URI names the queue elsewhere.
  - If instead the broker fans one envelope out to several queues (an exchange bound to several queues), the same
    envelope id arrives on several endpoints. Wolverine's source then recommends `MessageIdentity.IdAndDestination`.
    That case is untested.
- The Kit's wire-name registry plugs into Wolverine as a process-global `IMessageTypeNaming`.
- **Test run and `global.json`.** The worktree that ran the Spike has no `global.json`. `dotnet test` used the main
  checkout's root `global.json`, in a directory above it, which selects the Microsoft.Testing.Platform runner. The same
  root file exists on `feat/wave-1`. The test project carries `xunit.v3` only. A run without that `global.json` was not
  attempted.
- **Packages.** The runtime package closure is the same as ADR 0004's, and its licence table applies.
