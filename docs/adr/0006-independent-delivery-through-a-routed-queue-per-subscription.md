---
status: accepted
---

# Each subscription gets its own routed queue and a pre-built Kit handler

Spike S2 compared ways to retry and acknowledge each Notification Handler and each Integration Event
Handler on its own, against WolverineFx 6.46.0 and PostgreSQL.

The Kit takes **Option C**. Each subscription gets one Wolverine route
(`PublishMessage(type).ToLocalQueue(subscriptionName)`), so a published Notification or Integration Event becomes one
durable envelope per subscription on that subscription's own queue. Each message type gets one pre-built Kit handler, registered with
`WolverineOptions.AddMessageHandler`, which generates no code. The handler finds the subscription from the queue the
envelope was delivered to, then runs the subscribed handler in a new scope.

What decides it is visibility, not size. Option A (Wolverine's separated queues) is the smallest by the count below,
25 lines against C's 27, but it needs `public` handlers, which ADR 0001's isolation by `internal` types rules out, and
it cannot deliver an `internal` Domain Event. Of the options compared, only C and the pre-built variant of the delivery
envelope (B) deliver an `internal` Domain Event to `internal` Notification Handlers, and C is far smaller (27 lines
against 48). C is also the only tested shape with no Kit envelope type and no generated code. Nothing observed
contradicts ADR 0001–0004 or section G.

## Evidence

The Spike code is in `spikes/s2-independent-delivery/` at commit `73acc25c25fe9dea957542985998fae078bde709`, reachable
through tag `spike/s2-independent-delivery`. It is not merged. The tests ran against `postgres:18.6` in Testcontainers,
with `track_commit_timestamp=on`. 43 of 43 tests passed. Each test gets a fresh database.

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
| Every subscription to a type removed | `SubscriptionChangeTests.A_pending_delivery_whose_type_lost_every_subscription_is_discarded_by_default` (C) | Inventory, the only subscription, is left with a pending delivery at a graceful stop. The next Host has no subscription to `ProductCreatedV1`. Wolverine, with its default `UnknownMessageBehavior.LogOnly`, marks the row `Handled`: the handler never runs and nothing is dead-lettered. The committed delivery is silently discarded. |
| Remedy: unknown types to the dead-letter queue | `SubscriptionChangeTests.A_pending_delivery_whose_type_lost_every_subscription_is_dead_lettered_when_unknown_messages_go_to_the_dead_letter_queue` (C) | The same with `UnknownMessageBehavior.DeadLetterQueue`: the row is dead-lettered at the old queue with `UnknownMessageTypeNameException` ("Unknown message type: 'catalog.product-created.v1'"), and never marked `Handled`. |
| Subscription renamed | `SubscriptionChangeTests.A_pending_delivery_whose_subscription_was_renamed_is_dead_lettered` (C) | The next Host subscribes the same handler under a new name. The row on the old queue is recovered and handed to the routed handler, whose name lookup throws `KeyNotFoundException`. The retry ladder runs and the row is dead-lettered. The renamed subscription never receives it. |
| Internal Domain Event across a graceful stop | `SubscriptionChangeTests.A_pending_internal_domain_event_is_delivered_by_the_next_host` (C) | Index's delivery of the internal `ProductCreated` is pending at a graceful stop. The next Host delivers it, and Announce, which had succeeded, does not run again. |
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
- **Unknown message types.** `src/Wolverine/WolverineOptions.cs:66-78`: `UnknownMessageBehavior.LogOnly`, the default,
  logs a received envelope with no known handler and discards it; `DeadLetterQueue` moves it to the receiving
  endpoint's dead-letter queue.
- **Other sources.** `src/Persistence/Wolverine.RDBMS/Durability/MoveReplayableErrorMessagesToIncomingOperation.cs`
  (replay). `src/Persistence/Wolverine.Postgresql/Schema/IncomingEnvelopeTable.cs:20-29` (primary key).
  `src/Wolverine/DurabilitySettings.cs:41-54` (`MessageIdentity`). `src/Wolverine/Util/WolverineMessageNaming.cs:142-152`
  (process-global naming strategies; `InsertFirst<T>()` requires `T : new()`).

## Comparison

**Counting rule.** An option's owned code is the lines of its own file, exactly as it ran, that are neither blank nor
`//` or `///` comments, after removing whole lines that only a control or a rejected variant uses (including a `using`
and the braces of a block that only that variant has). No line is rewritten or merged; a line that the judged
configuration runs stays, even if it also tests a variant flag. The stand-ins the options share are counted once,
apart:

| Shared stand-in | Lines |
|---|---|
| Result, the two handler interfaces, the exception | 17 |
| Registration and `Subscription.DeliverAsync` | 47 |
| Wire names | 19 |
| Unit of Work | 32 |

They are not used identically. In the registration file, the case-insensitive name map and `Named` serve C, B and A
with key types, and `For` serves A, A with key types and B. Each of those is one line, so a figure below would move by
a line or two if they were charged to the options.

These lines were removed before counting, by line number in the file at the Spike commit:

| Option | File | Removed | Owned lines |
|---|---|---|---|
| A | `OptionA/SeparatedQueues.cs` | 23 (the Classic control's arm of the ternary) | 25 |
| A with key types | `OptionA/SeparatedSlots.cs` | none | 31 |
| B | `OptionB/DeliveryEnvelope.cs` | 30-31 and 50 (`EndpointFor`); 3-4, 38, 56-60, 64-66 and 76-84 (pre-built) | 34 |
| B pre-built | `OptionB/DeliveryEnvelope.cs` | 30-31 and 50 (`EndpointFor`); 39 and 69-73 (generated handler) | 48 |
| C | `OptionC/RoutedQueues.cs` | 5, 23-24 and 28-34 (TCP) | 27 |

| | C: routed queues | A: separated, handler type | A: separated, key types | B: envelope | B: envelope, pre-built |
|---|---|---|---|---|---|
| Owned lines | 27 | 25 | 31 | 34 | 48 |
| Independent retry and acknowledgment (Integration Event Handlers) | Yes | Yes, public handlers only | Not run (only delivery was tested) | Yes | Not run |
| Internal Integration Event Handler | Delivered | Dead-lettered (CS0122) | Delivered | Delivered | Not run |
| Internal Domain Event to internal Notification Handlers | Delivered, independent retry | Not run (its generated code names both internal types) | Dead-lettered (CS0122) | Dead-lettered (CS0122) | Delivered, independent retry |
| Application handler written against Wolverine | No | No | No | No | No |
| Generated code | None | One chain per subscription | One chain per subscription | One chain per message type | None |
| Queues | One per subscription | One per subscription | One per subscription | One per message type | One per message type |
| Envelope rows per published Notification or Integration Event | One per subscription | One per subscription | One per subscription | One per subscription | One per subscription |
| Subscription visible in | `received_at` | `received_at` | `received_at` | Body only | Body only |
| `message_type` | Contracts wire name | Contracts wire name | Contracts wire name | `kit.delivery.<wire name>` | `kit.delivery.<wire name>` |
| A single subscription, then a second | No special case | Needs an explicit route | Needs an explicit route | No special case | No special case |
| Wolverine handler type (by construction) | `RoutedHandler`, one per message type | `SeparatedHandler<THandler, TMessage>` | `SlotHandler<TMessage, Slot<Slot<…>>>` | `HandlerDeliveryHandler<T>` | `PrebuiltDeliveryHandler` |

The Feature author writes the same thing under every option: the handler class and one `Subscribe` or
`SubscribeNotification` call in the Composition Root. Option A alone also requires the class to be `public`.

By the Spike's rule (independent retry and acknowledgment, no application-specific handler class written against
Wolverine, least owned code), A is the smallest, but only with `public` handlers, which ADR 0001 rules out, and it
cannot deliver an `internal` Domain Event. A with key types and B fail that last point too. Between the two options
that pass every point, C (27) and B pre-built (48), C has the least owned code.

## The registration and handler code that worked

Composition Roots. The Notification wire name and subscription names are declared explicitly; see "Naming
Notifications" below.

```csharp
messaging.Subscribe<ProductCreatedV1, ReserveStockOnProductCreated>("inventory.product-created");   // Inventory
messaging.Publish<ProductCreated>("catalog.product-created");                                         // Catalog, internal Domain Event
messaging.SubscribeNotification<ProductCreated, IndexProductOnProductCreated>("catalog.product-created.index");
```

Option C, abridged from `S2.Kit/OptionC/RoutedQueues.cs`, inside `UseWolverine`. The TCP branch and the `foreach`
braces it needs are left out; the count above measures the file as it ran.

```csharp
foreach (var s in messaging.Subscriptions) opts.PublishMessage(s.MessageType).ToLocalQueue(s.Name);
foreach (var messageType in messaging.Subscriptions.Select(s => s.MessageType).Distinct())
    opts.AddMessageHandler(messageType, new RoutedHandler(messaging));
// The Unit of Work publishes each Notification or Integration Event with outbox.PublishAsync;
// Wolverine writes one envelope per route.

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
and its Unit of Work transaction, inserts the Receipt keyed by **(subscription name, the Notification's or Integration
Event's identity)**, runs the handler, and commits the Receipt and the effect together. That identity is the Domain
Event's id, which an Integration Event carries (G.2).

The key cannot be the Wolverine envelope id. That id differs per route, as `AcknowledgmentTests` shows with two distinct
ids for one Integration Event. Under Option C the subscription name comes from the queue the envelope was delivered to.

The Spike built no Receipts. The pre-built handler runs no Wolverine middleware, so Wolverine marks the envelope
`Handled` after `HandleAsync` returns, separately from the effect. That follows from the construction and from ADR
0004's EF-mode test; S2 did not test it again. A crash in between therefore redelivers, and the Receipt makes the
redelivery a no-op. Concurrent duplicate hand-outs, from `InboxStaleTime` as in ADR 0004 and as below, need the same
Receipt.

## Naming Notifications

Plan section D declares neither a wire name for a Domain Event delivered as a Notification nor a durable subscription
name for a Notification Handler. Both are needed:

- The Domain Event's wire name goes into `message_type` and resolves the type when the envelope is read back.
  `WireNames.Of` throws for a type that has none.
- The subscription name is the queue name and the Receipt's key.

The Spike declared both explicitly in Catalog's Composition Root. The Kit has to choose between explicit declaration and a
convention such as `<module>.<event>` and `<module>.<event>.<handler>`. Startup must fail when a published Notification
has no wire name or a subscribed handler has no subscription.

A renamed subscription dead-letters its pending deliveries (`A_pending_delivery_whose_subscription_was_renamed_is_dead_lettered`).
Names derived from CLR type names would therefore make a class rename a breaking change for deliveries in flight.

## Subscription names

These follow from Option C, not from preference:

- **Unique ignoring case.** Wolverine lower-cases local queue names, so two names that differ only in case would share
  one queue. The Spike's registration already rejects them.
- **Valid as a URI host.** C reads the subscription back from the queue URI's host. Names that are not valid hosts
  were not tested; the Kit should reject them at startup.
- **A Kit-named error for an unknown subscription.** Today the routed handler's lookup throws a bare
  `KeyNotFoundException` for a queue that no subscription names (the rename test). It should throw a Kit exception that
  names the queue and says the subscription is unknown.

*Both answered: owner decision 3 below, and `UnknownSubscriptionException`
(`NotificationTests.Startup_fails_on_a_missing_an_invalid_or_a_duplicate_name`,
`NotificationTests.A_stored_delivery_to_a_subscription_the_host_no_longer_declares_is_dead_lettered_with_a_kit_error`).*

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
  several endpoints, was not tested; see Consequences on brokers. *Tested: required for one broker queue
  per subscription, not for the topology chosen; see the RabbitMQ topology amendment below.*
- **What the TCP test does not show.** It runs sender and receiver in one process. It shows independent retry and the
  endpoint-to-subscription mapping over a non-local transport. It does not show that the sender's envelopes are written
  in the Unit of Work's commit; that was shown for local queues only (`AcknowledgmentTests`).
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

  Not tested: a persisted retry that fires after `InboxStaleTime` keeps its old insert time, so once it is owned again
  it is already past the stale time. A run longer than one recovery poll could then be handed out concurrently; that is
  inferred from the same SQL. *Tested; see the persisted retry and stale time amendment below.*
- **Attempt count.** With `RetryWithCooldown` the `attempts` column stayed 0 after a failure, after a kill and after a
  graceful stop, so in-memory retries start counting again after a crash. `ScheduleRetry` persisted 1.
- **A removed subscription or type.** With Wolverine's default `UnknownMessageBehavior.LogOnly`, a stored delivery
  whose type no longer has any subscription is marked `Handled` without running anything. With
  `UnknownMessageBehavior.DeadLetterQueue` it is dead-lettered instead. A stored delivery whose subscription was
  renamed fails the name lookup and is dead-lettered after the retry ladder. A subscription removed while its type
  keeps others takes the same code path; that case was not tested separately. *Tested (2026-10-10) on 6.48.3; see
  the delayed commands amendment's 2026-10-10 "Pinned" note on a command type no declaration names, below.*
- **Lazy code generation.** Generated chains compile at first delivery, not at Host start. Option C generates none, but
  the Kit still needs its own startup check of every subscription.

## Invariants for permanent tests

| Invariant | Seam |
|---|---|
| One published Notification or Integration Event writes one durable envelope per subscription in the Unit of Work's commit, each on its subscription's queue | Gateway |
| No local send through `EndpointFor` inside the Unit of Work | Structure |
| Every published Notification type has a wire name and every Notification Handler a subscription name, or startup fails | Gateway |
| An `internal` Domain Event reaches `internal` Notification Handlers; Notification Handlers and Integration Event Handlers may be `internal` and reference no Wolverine type | Gateway and Structure |
| A failing handler never re-runs one that succeeded: across retries and across a kill and restart, for two Integration Event Handlers in different Modules (Inventory and Billing in the Proving Sample), and for two Notification Handlers of one Domain Event in one Module | Gateway |
| A failed Result is retried and never acknowledged; each attempt runs in a new scope | Gateway |
| Every subscription resolves to a handler at startup, or startup fails | Gateway |
| A redelivered or concurrently duplicated envelope applies its effect once, through the Receipt keyed by (subscription, the Notification's or Integration Event's identity) | Gateway |
| A stored delivery whose subscription or type no longer exists is dead-lettered, never marked `Handled`: `UnknownMessageBehavior.DeadLetterQueue`, and a Kit-named error for an unknown subscription | Gateway |
| Exhausted retries move only that subscription's envelope to dead-letter storage, never marked `Handled`. A replay runs only that handler. The dead letter is found by `received_at` | Gateway |
| With `InboxStaleTime` set, a delivery waiting to retry is not handed out again and does not run again after it succeeded. Use a persisted retry, or keep the whole retry ladder plus handling below the stale time | Gateway |

## Owner decisions (2026-10-05)

These follow the decision above and do not reverse it.

1. **Retry policy.** The Kit's default retry is the stored retry, `ScheduleRetry`. The Kit ships no in-memory
   retry ladder unless a later requirement clearly needs one.
2. **Naming Notifications.** The wire name and the subscription name are derived by convention by default, and
   each has an explicit, stable override. Renaming a type or a handler that uses the derived name is a breaking change
   for durable deliveries in flight; the override is how a durable identity survives a refactor.
3. **Subscription names.** A name must be valid as an endpoint and queue name and unique ignoring case. An invalid
   or duplicate name fails startup. The protection for removed subscriptions stays: a pending committed delivery is
   never silently treated as handled.
4. **Overall.** Prefer the smallest implementation that satisfies these guarantees. Add no abstraction or
   configurability until a later requirement proves it is needed.

## Considered Options

- **A, separated queues with the handler type in generated code.** Rejected, although it is the smallest (25 lines). It
  cannot deliver to an internal handler or an internal Domain Event, so it needs `public` handlers, which ADR 0001 rules
  out. A single subscription also needs an explicit route.
- **A with key types.** Rejected. It cannot deliver an internal Domain Event, because the message type is still in
  generated code. It is 31 lines, it needs the single-subscription route, and Wolverine's logs name nested `Slot<…>`
  types.
- **B, a Kit delivery envelope with a generated handler.** Rejected. It cannot deliver an internal Domain Event, because
  its generated handler is closed over the payload type. It uses one queue per message type, so the subscription is
  visible only in the body.
- **B with a pre-built handler.** It works, but it is rejected: it is Option C's mechanism plus a Kit envelope type, at
  48 lines against 27.
- **B through `EndpointFor`, and A without the single-subscription route.** Rejected for the observed failures above.

## Consequences

- `PouryaKit.Messaging.Wolverine` owns the route per subscription and one pre-built `MessageHandler` per subscribed
  message type, together with the shared delivery step (scope, Unit of Work, Receipt, failed Result to exception).
  `PouryaKit.Messaging` keeps the transport-neutral `Publish`, `Subscribe` and `SubscribeNotification` declarations and
  the handler interfaces.
- The Kit relies on `AddMessageHandler`, which Wolverine labels "for advanced usages". No Wolverine middleware runs for
  these handlers. Wolverine's failure policies do run, as the tests show. The Gateway tests in the invariants table are
  what will catch a change in a later Wolverine version.
- **One durable local queue per subscription.** That is a cost: the number of listening queues, with their polling and
  recovery work, grows with the number of subscriptions rather than with the number of message types.
- Subscription names are durable identifiers: they are queue names, they appear in `received_at` of stored envelopes,
  and they are half of the Receipt key. Renaming one dead-letters its pending deliveries after the retry ladder; the
  renamed subscription never receives them.
- **A Kit Host sets `UnknownMessageBehavior.DeadLetterQueue`.** With Wolverine's default, removing every subscription to
  a type silently discards its committed, pending deliveries.
- **Brokers in Wave 2.**
  - The bare Notification or Integration Event is what is stored and sent, under its own wire name. The TCP test shows
    it arriving with the Contracts wire name in `message_type`.
  - A broker queue per subscription keeps this shape: one route per subscription, and the Kit maps the receiving
    endpoint to its subscription by that endpoint. The map is the only Kit-owned addition. It cannot be the URI host,
    which for TCP was `localhost`; a broker URI names the queue elsewhere.
  - With separate Hosts the broker, not the sender's routing, does the fan-out (an exchange bound to one queue per
    subscription). One envelope id arriving on several endpoints is then the expected case, not the exception, and
    Wolverine's source recommends `MessageIdentity.IdAndDestination` for it. That case is untested. *Answered by the RabbitMQ topology
    amendment below: the Kit takes one broker queue per Host, fanned out to these local queues, under `IdOnly`.*
- The Kit's wire-name registry plugs into Wolverine as a process-global `IMessageTypeNaming`.
- **Packages.** The runtime package closure is the same as ADR 0004's, and its licence table applies.
- **A Notification Handler publishing an Integration Event.** Under Option C, a Notification Handler published an Integration Event
  through a Unit of Work opened inside its delivery scope. The Spike has no test of this. *Tested:
  `IntegrationEventTests.An_integration_event_is_stored_in_the_commit_of_the_publishing_notification_handlers_receipt`.*

## Amendment (2026-10-05: the declarations and the names used)

The decision above stands. Implementing it departed from one consequence and settled what the record left open.

- **Where the declarations live.** The Consequences say `PouryaKit.Messaging` keeps the `Publish`, `Subscribe` and
  `SubscribeNotification` declarations. They live in `PouryaKit.Messaging.Wolverine` instead: they are registration
  calls on a Module's builder, and plan section C gives `PouryaKit.Messaging` no dependency-injection reference.
  `PouryaKit.Messaging` keeps the handler and publisher interfaces, the name rules and the header names. Neither
  package references `PouryaKit.Modularity`, so each declaration takes the Module's definition type as an argument.
  *Changed on 2026-10-07: the declarations read the Module from Cqrs's `IModuleServices`, which a Module's
  builder implements, and take no Module argument; see the amendment of that date.*
- **The names used.** `AddPouryaKitNotificationHandlers(assembly, module)` replaces `SubscribeNotification`: it
  subscribes every Notification Handler of an assembly, under a derived subscription name unless the handler declares
  one (owner decision 2). `Publish<TIntegrationEvent>(wireName, module)` declares an Integration Event's wire name and
  publishing Module, and refuses a Domain Event, whose wire name is derived or declared on the type.
  `Subscribe<TIntegrationEvent, THandler>(subscriptionName, module)` subscribes one Integration Event Handler.
- **Subscription names are stored lower-cased**, as their local queues are, so a change of case only is the same
  subscription, Receipts included.
- **Reserved queue names are refused.** A subscription may not be named after a local queue Wolverine 6.46.0 keeps for
  itself: `agents`, `default`, `durable`, `replies`, `scheduled` or `system`.
- **The startup check.** Once every hosted service has started, a Host that declares Notification Handlers,
  Integration Event Handlers or published Integration Events fails unless an Outbox writes them in the Unit of Work's
  commit and every subscription's queue, as Wolverine built it, is a durable application queue.

## Amendment (2026-10-05: a persisted retry and the stale time)

The decision above stands. Permanent tests decided what the record inferred about the inbox stale time,
and refine one of its invariants.

- **A persisted retry and a short schedule, not one or the other.** The invariant on the stale time reads "use a persisted
  retry, or keep the whole retry ladder plus handling below the stale time". It is "and". A persisted retry keeps a
  delivery that waits for its retry from being handed out again, because the row is owned by no node while it waits.
  It does not reset the insert time the release compares, so once the delivery is older than `InboxStaleTime`, an
  attempt that runs is released while it runs and handed out again, concurrently. The Kit keeps the stored retry (owner
  decision 1) and requires `InboxStaleTime` to exceed the sum of the retry delays plus every attempt's run, queue wait
  included.
- **The "Not tested" inference held.** A persisted retry that fires after `InboxStaleTime` is now tested against
  PostgreSQL, with a 500 ms stale time and one retry after 1 s. In
  `InboxStaleTimeTests.A_retry_that_runs_after_the_stale_time_is_handed_out_again_while_it_runs_and_its_receipt_keeps_one_effect`
  the retry is held after its Receipt, no row is touched, and the store hands out a copy that waits on the Receipt's
  lock; the handler ran twice, the failed attempt and the retry, and one effect committed. In
  `InboxStaleTimeTests.A_delivery_waiting_for_its_retry_is_not_handed_out_again_and_one_that_succeeded_never_runs_again`,
  with the same settings, the retry waits out its whole delay, twice the stale time, without being handed out, and
  neither handled delivery runs again.
- **The default delays were shortened to fit.** `PouryaKitMessagingOptions.RetryDelays` was 1 s, 10 s, 1 min and
  10 min, whose sum alone exceeds the default 5-minute `InboxStaleTime`. It is now 1 s, 10 s and 1 min: four attempts
  and 71 s of waiting.
- **No dead-letter API of the Kit's own.** A dead letter stays in Wolverine's dead-letter storage, found by its
  subscription in `received_at` and replayed there (owner decision 4). The Kit
  adds only the reason a failed Result gives: `NotificationHandlerFailedException` and
  `IntegrationEventHandlerFailedException` name each Error's code and message. `PouryaKit.Testing` reads and replays
  dead letters through Wolverine's `IDeadLetters`, as an application does.
- **A subscription removed while its type keeps another**, which "A removed subscription or type" above says was not
  tested separately, is now tested:
  `FailingDeliveryTests.A_stored_delivery_to_a_removed_subscription_is_dead_lettered_while_the_remaining_subscription_handles_its_own`.

## Amendment (2026-10-06, Wave 1 as built)

The decision above stands. The formats below, which stored rows and every receiver now
depend on, are fixed, and so are the rules a consuming project must keep.

- **Stored formats.** Each Module keeps its Receipts in its own schema, in the table `pouryakit_receipts`
  (`subscription` text, `message_id` uuid, both the primary key), which a Module migration creates with
  `CreateReceiptTable(schema)`; it is not part of the Module's EF Core model
  (`src/PouryaKit.Messaging.Wolverine.EFCore/ReceiptTable.cs`). A publishing Module keeps there, too, one row per
  Domain Event and Integration Event type it published, keyed `<wire name, lower-cased>#published` and the Domain
  Event's `Id`; a subscription name cannot contain `#`, so the two never collide. A Module that subscribes to a
  Snapshot keeps the newest version each subscription applied per aggregate in `pouryakit_snapshot_versions`
  (`subscription`, `aggregate_id` text, `version` bigint; key `subscription`, `aggregate_id`), created with
  `CreateSnapshotVersionTable(schema)` (`SnapshotVersionTable.cs`).
- **Snapshot identity.** A Snapshot's identity, the Receipt's key for it, is a name-based UUID (RFC 9562 version 8)
  from the SHA-256 of the lower-cased wire name, the aggregate's key and the version, so every delivery of one version
  repeats it and two versions never share it (`src/PouryaKit.Messaging.Wolverine/SubscriptionDelivery.cs`). Changing
  the formula would make Receipts already stored miss redeliveries. The key is unique only within one mapped
  hierarchy, so a declaration covers the aggregates of one: when its aggregate type spans two, the Operation that
  would publish it fails (`SnapshotMappers.CheckKeySpaces`;
  `SnapshotTests.A_snapshot_declared_for_aggregates_of_two_mapped_hierarchies_fails_the_operation_that_would_publish_it`).
- **"Strictly newer" is the Kit's.** Plan section D's sketch left the version check to the receiving Feature. The
  Kit's delivery step does it instead, after the Receipt and in the same transaction, with one conditional upsert that
  replaces the stored version only with a greater one; the handler runs only when it did. If the older concurrent
  version commits first, both handlers can run in increasing version order; if the newer commits first, the older is
  skipped. A Snapshot no newer than the committed version is never applied (`src/PouryaKit.Messaging.Wolverine/Deliver.cs`, `EfReceipts.cs`;
  `SnapshotTests.Snapshots_delivered_in_reverse_order_leave_the_newest`,
  `SnapshotTests.Two_versions_delivered_concurrently_leave_the_newer`,
  `SnapshotTests.A_live_snapshot_delivered_after_the_tombstone_does_not_bring_the_copy_back`).
- **The mapper.** `ISnapshotMapper<TAggregate, TSnapshot>` has two methods, `Map` for an aggregate that exists and
  `Tombstone` for one the Operation removed, and the declaration names it:
  `PublishSnapshot<TAggregate, TSnapshot, TMapper>(wireName, module)`. Plan section D sketched one method and two type
  parameters.
- **One Integration Event of each type per Domain Event.** The Outbox records each publication in the publishing
  Module's Receipt table, in the delivery's transaction, so when two Notification Handlers of one Domain Event publish
  the same type, the second delivery to commit fails instead of sending a second event every receiver would drop
  (`IntegrationEventTests.A_second_notification_handler_of_one_domain_event_publishing_the_same_type_fails_instead_of_being_dropped`).
- **No subscription, nothing written.** The Outbox writes no envelope for a Notification, Integration Event or Snapshot
  that no subscription in the Host receives: it has no route (`WolverineOutbox.cs`; *since 2026-10-18,
  `OutboxWriter.cs` of `PouryaKit.Messaging.Wolverine`, which the EF Outbox calls*). This changes when brokers arrive in
  Wave 2: a subscriber may then live in another Host, so the publishing Host can no longer tell an unsubscribed type
  from one subscribed elsewhere. *Changed by the RabbitMQ topology amendment below: a published Integration Event gets a broker route from its
  publication.*
- **What a consuming project must keep.**
  - The key of an aggregate that publishes Snapshots is never used again after the aggregate is deleted outright:
    every receiver keeps its Tombstone's version and would turn away the new aggregate's lower ones. The Kit's Guid
    version 7 keys never are.
  - An aggregate whose key the database generates cannot publish a Snapshot: the key is unknown when the Snapshot is
    built, so the Operation that creates it fails
    (`SnapshotTests.A_snapshot_of_an_aggregate_whose_key_the_database_generates_fails_the_operation_that_creates_it`).
    *Since 2026-10-10, so does an aggregate with any other value the database generates in its mapped
    graph; see ADR 0003's amendment on values the database generates.*
  - Nothing removes a Receipt or a Snapshot-version row, so both tables grow with the messages a Module receives.
    *Receipts are kept on purpose since 2026-10-10, with the publication records; see the Receipts and publication records amendment below.*

## Amendment (2026-10-07: RabbitMQ topology)

The decision above stands, and the broker extends it. A Spike compared two RabbitMQ topologies with a
publishing Host and a subscribing Host in separate databases. The Kit takes **topology B, receive once and then route
locally**. Each subscribing Host has one durable broker queue, named by its `WolverineOptions.ServiceName` and bound to
the exchange of every wire name it subscribes to, with a durable Inbox. The Kit's pre-built handler publishes each
envelope received there again inside the Host. From there it reaches one durable local queue per subscription, exactly
as above.
**Topology A, one broker queue per subscription,** is rejected.

Both topologies kept every invariant asked for under a fair configuration, so these other observed
differences decide:

- **Inbox identity.** A needs `MessageIdentity.IdAndDestination`, because the broker copies one envelope id to every
  subscription's queue. With the default `IdOnly`, the copy stored second is taken for a duplicate and acknowledged
  without running. The setting is Host-wide and changes the primary key of Wolverine's incoming-envelope and dead-letter
  tables. B keeps `IdOnly` and the Wave 1 store unchanged.
- **Everything after the broker stays as built.** Under B the subscription is still read from the local queue, and
  dead letters are still found at `local://{subscription}/`. The stored retry and the stale-time rule of the persisted retry and stale time
  amendment apply as they are. Under A the subscription comes from a broker queue address, and a dead letter's
  `received_at` becomes `rabbitmq://queue/{subscription}`.

Queue names do not decide between them. In the Spike, A's queue is named by the subscription alone
(`Rmq.Kit/BrokerTopology.cs:63-64`), so two Hosts that declare the same subscription names split the messages
(`QueueNamingTests`, case A). With A's queue named `{ServiceName}.{subscription}` on a copy of the Spike, each Host
received all 6 messages, as under B. B carries the same risk one
level up: two different Hosts with the same `WolverineOptions.ServiceName` share one broker queue and would split its
messages. That is inferred from the case-A observation and not tested for B. The Kit checks neither name across Hosts.

B's cost is one more stored envelope per message and Host: three Inbox rows for two subscriptions, against two under
A. B also has one non-atomic step, observed and covered by the Receipt: the local copies are stored before the
broker envelope is marked handled. A subscribing Host killed between the two fans out again after it restarts, and
the Receipts keep each effect to one. The Spike forced that gap open with an explicit
`context.FlushOutgoingMessagesAsync()` followed by a test hook (`Rmq.Kit/SubscriptionDelivery.cs:75-79`). Without
them, Wolverine flushes the copies after the handler returns and only then marks the envelope handled
(`src/Wolverine/Runtime/MessageSucceededContinuation.cs:22-24` at `V6.46.0`; read, not tested). That is the order the RabbitMQ package
relies on. Nothing observed contradicts ADR 0001–0004 or section G.

### Evidence

The Spike code is in `spikes/rabbitmq-topology/` at commit `0c3ef9b2aef914ba30f040f07dd51c25ee6b3720`, reachable
through tag `spike/rabbitmq-topology`. It is not merged. The tests ran against `rabbitmq:4.3.6` and `postgres:18.6`
(with `track_commit_timestamp=on`) in Testcontainers. 19 of 19 tests passed.
Each test gets two fresh databases and a RabbitMQ vhost of its own.

- **Publishing Host (`catalog`).** Catalog publishes `ProductCreatedV1` (wire name `catalog.product-created.v1`, a
  Contracts record) through the owned Unit of Work of ADR 0004. No subscription in its own Host receives it. The route
  comes from the publication: one fanout exchange per wire name with a durable outbox.
- **Subscribing Host (`inventory`).** Inventory subscribes two `internal` Integration Event Handlers to that wire name.
  `inventory.reserve-stock` always succeeds. `inventory.audit-product` writes its effect and then returns a failed
  Result for as many attempts as the test sets. Each delivery runs as in the Kit:
  - one pre-built handler per message type, registered with `AddMessageHandler`, so no code is generated;
  - a new scope and one transaction on the subscribing Host's database;
  - the Receipt, keyed by (subscription, the `pouryakit-domain-event-id` header);
  - the handler, then commit;
  - a failed Result rolls back and throws, so the delivery is retried.

  Every attempt's outcome is logged on a separate connection, because a Receipt masks a rerun.
- **Shared settings.** Durability mode `Solo` with short polling timers. The Kit's stored retry
  (`ScheduleRetry`, then `MoveToErrorQueue`), with `UnknownMessageBehavior.DeadLetterQueue` and
  `UseDurableLocalQueues`. RabbitMQ is configured with `AutoProvision()` and `DisableDeadLetterQueueing()`, so dead
  letters go to Wolverine's storage and not to a broker queue.
- **A's fair configuration.** One durable queue per subscription, named by the subscription alone and bound to the wire
  name's fanout exchange, each listened to with `UseDurableInbox()`. The endpoint map sends
  `rabbitmq://queue/{subscription}` to its subscription. `MessageIdentity.IdAndDestination` is set, except in the
  `IdOnly` control.
- **B's configuration.** One durable queue per Host, named by its `WolverineOptions.ServiceName`, bound to each
  subscribed wire name's exchange and listened to with `UseDurableInbox()`. One local route per subscription, as above.
  *(2026-10-11: "per Host" here means per kind of Host: the instances of one Host share the queue its service name names.)*
  `IdOnly`.

| Point | Test (class.method, cases) | A, one queue per subscription | B, receive once then local queues |
|---|---|---|---|
| Remote-only publication | `RemoteOnlyPublicationTests.A_publication_with_no_subscriber_in_its_own_host_is_stored_in_the_commit_and_delivered_to_the_other_host` (A, B) | Before the flush there is one row in the publishing Host's `wolverine_outgoing_envelopes`, with the business row's `pg_xact_commit_timestamp`. Both subscriptions commit one Receipt and one effect, and the outgoing row is deleted once sent. | The same. |
| Control: no broker route | `RemoteOnlyPublicationTests.Without_a_route_from_the_publication_to_the_broker_nothing_is_written_and_nothing_arrives` | No outgoing row is written. The business row commits, and nothing arrives within 3 s. This is today's Kit, which routes only to local subscriptions. | |
| Independent retry, no rerun, no starvation | `IndependentRetryTests.A_failing_subscription_is_retried_on_its_own_and_never_reruns_or_starves_the_other` (A, B; 3 messages; audit fails twice; retries 1 s apart) | All three `reserve-stock` Receipts commit while `audit-product` has no Receipt and no effect. Then, per message, `reserve-stock` has attempts `[succeeded]` and `audit-product` has `[failed, failed, succeeded]`. Each has 1 effect and 1 Receipt, the Receipt with the effect's commit timestamp, and there is no dead letter. | The same. |
| Exhausted retries | `DeadLetterTests.Exhausted_retries_set_aside_only_the_failing_subscriptions_delivery_in_wolverine_storage` (A, B) | One dead letter in `wolverine_dead_letters` at `rabbitmq://queue/inventory.audit-product`, with the wire name and `HandlerFailedException`. Audit has 3 failed attempts and no effect, `reserve-stock` applied once, and there is no broker dead-letter queue. | The same, at `local://inventory.audit-product/`. |
| Durable Inbox across a kill and restart | `DurableInboxTests.A_delivery_waiting_for_its_retry_is_kept_by_the_inbox_across_a_consumer_kill_and_restart` (A, B; audit fails once; retry after 4 s) | The waiting delivery is a `Scheduled` row at `rabbitmq://queue/inventory.audit-product`, and both broker queues hold 0 messages. The subscribing Host stops with `StopMode.Quick`. A second publication waits in each broker queue (1 message). A new subscribing Host on the same database delivers both. Attempts are `reserve-stock` `[succeeded]` and audit `[failed, succeeded]`, with 1 effect each per product. | The same, with the waiting row at `local://inventory.audit-product/` and the second publication waiting in the queue `inventory`. |
| Redelivery after handling | `RedeliveryTests.A_broker_envelope_handed_out_again_after_it_was_handled_applies_each_effect_once` (A, B) | Setting the handled broker rows back to `Incoming` with owner 0 hands both out again. Attempts are `[succeeded, duplicate]` per subscription, with 1 effect and 1 Receipt each. | The one broker row is handed out again and fans out again. There are 4 local rows, and the attempts, effects and Receipts are the same. |
| B's non-atomic fan-out | `RedeliveryTests.Receive_once_killed_between_the_fan_out_and_marking_the_broker_envelope_handled_fans_out_again_and_applies_each_effect_once` (B) | | The test holds after the local copies are stored. Both Receipts commit, and the broker row is still `Incoming` with a non-zero owner. After the kill and a restart the row is recovered and fans out again. There are 4 local rows, and the broker row is `Handled`. Attempts are `[succeeded, duplicate]`, with 1 effect each. |
| Fan-out identity, `IdOnly` (control) | `FanOutIdentityTests.Queue_per_subscription_with_IdOnly_keeps_one_copy_and_acknowledges_the_other_as_a_duplicate` | Both broker queues drain, but only 1 Receipt and 1 attempt exist in total, and the Inbox has 1 row. Wolverine logs `DuplicateIncomingEnvelopeException` for the other queue. One subscription silently never runs. | |
| Fan-out identity | `FanOutIdentityTests.Queue_per_subscription_with_IdAndDestination_keeps_one_envelope_id_once_per_subscription_queue`; `FanOutIdentityTests.Receive_once_stores_the_broker_envelope_once_and_gives_each_local_copy_an_id_of_its_own` | 2 Inbox rows with **one** envelope id, at `rabbitmq://queue/inventory.audit-product` and `rabbitmq://queue/inventory.reserve-stock`, both with the Contracts wire name in `message_type`. | 3 Inbox rows with **three** ids: `rabbitmq://queue/inventory`, `local://inventory.audit-product/` and `local://inventory.reserve-stock/`, all with the wire name. Both Receipts carry the publishing Host's Domain Event id, so the header crossed the broker and the fan-out. |
| Queue names across Hosts | `QueueNamingTests.Two_services_that_declare_the_same_subscription_names_each_receive_every_message_only_when_the_broker_queue_is_the_hosts` (A, B; two Hosts with `ServiceName` `inventory` and `warehouse`, each with its own database; 6 messages) | With A's queue named by the subscription alone, the two Hosts' `reserve-stock` Receipts add up to 6, and each Host has fewer than 6. With the queue named `{ServiceName}.{subscription}`, 6 per Host were observed on a copy of the Spike. | Each Host has 6. |
| Before any subscribing Host declared its queues (limitation) | `RemoteOnlyPublicationTests.A_publication_before_the_subscribing_host_ever_declared_its_queues_is_dropped_by_the_broker` (A, B) | The publishing Host's exchange has no bound queue, so the broker accepts the message and the outbox deletes its row. A subscribing Host started afterwards receives nothing within 4 s, and its Inbox stays empty. | The same. |

**The tests discriminate.** Each line below is a run with one behaviour switched off. The scratch runner copied the
file, applied the change, ran the class and restored the file.

- Under A with `IdOnly`, the remote-only test failed: the Receipt wait timed out after
  `Duplicate incoming envelope ... at rabbitmq://queue/inventory.reserve-stock`.
- With the Receipt's result ignored, all three `RedeliveryTests` cases failed with 2 effects instead of 1.
- With `UseDurableInbox()` replaced by `BufferedInMemory()`, `DurableInboxTests` (A) failed: no `Scheduled` row was
  left in the Inbox. Under B the same test still passed, because the waiting retry is in the durable local queue. The
  broker listener's Inbox in B covers only the step from receipt to fan-out. A durable Inbox on every
  listener is required all the same.

These points come from reading the source of `jasperfx/wolverine` at tag `V6.46.0` (commit
`1f22835a2b3e32dbf4b75c7a5273622ee74a4929`). They are inferred and were not tested on their own:

- **Duplicate handling.** `src/Wolverine/Runtime/WorkerQueues/DurableReceiver.cs:812-819` and the method at `:874`: a
  received envelope whose Inbox insert hits the primary key is acknowledged and dropped. That explains the `IdOnly`
  control. Under B, a broker redelivery or an outbox resend of the same envelope id is dropped the same way while its
  handled row is kept (`KeepAfterMessageHandling`). After that, it is stored and fanned out again, and the Receipts
  absorb it.
- **Settlement order.** `:854-869`: the broker delivery is settled after the Inbox insert commits and before the
  handler runs. That is why the broker queues read 0 while a retry waits.
- **Identity changes the keys.** `src/Persistence/Wolverine.Postgresql/Schema/IncomingEnvelopeTable.cs:22-29` and
  `DeadLettersTable.cs:19-26`: `IdAndDestination` adds `received_at` to the primary key of both tables.
  `src/Wolverine/DurabilitySettings.cs:38-54` recommends it when "a single Wolverine process receive[s] the same message
  from multiple external listeners".
- **Broker dead-lettering.** `src/Transports/RabbitMQ/Wolverine.RabbitMQ/Internal/RabbitMqTransport.cs:379-392` and
  `RabbitMqTransportExpression.cs:283-288`: without `DisableDeadLetterQueueing()`, Wolverine declares a native broker
  dead-letter queue (`DeadLetterQueue.cs`, `Mode = Native` by default). Exhausted deliveries would then leave
  Wolverine's storage.
- **Recovery of a broker row.**
  `src/Persistence/Wolverine.RDBMS/Durability/CheckRecoverableIncomingMessagesOperation.cs` resolves the listener
  circuit from `received_at`. That is how a `Scheduled` or recovered row at `rabbitmq://queue/...` returns to its
  listener.
- **Queue addresses.** `src/Transports/RabbitMQ/Wolverine.RabbitMQ/RabbitMqEndpointUri.cs`: a queue listener's address
  is `rabbitmq://queue/{name}`. Its `Host` is `queue`, so the Kit's lookup by `Destination.Host` cannot name a broker
  subscription.

### Packages and licences

Versions and licences come from nuget.org and from the restored nuspecs, read on 2026-10-07.

| Package | Version | Licence | Role |
|---|---|---|---|
| WolverineFx.RabbitMQ | 6.46.0 (current stable, same release as the Kit's WolverineFx; `net10.0` restore and run) | MIT | runtime, for the RabbitMQ package |
| RabbitMQ.Client | 7.2.2 (transitive) | `Apache-2.0 OR MPL-2.0`; the Kit takes it under Apache-2.0 | runtime |
| WolverineFx.Newtonsoft | 6.46.0 (transitive) | MIT | runtime |
| Newtonsoft.Json | 13.0.3 (transitive) | MIT | runtime |
| System.Threading.RateLimiting | 8.0.0 (transitive, from RabbitMQ.Client) | MIT | runtime |
| Testcontainers.RabbitMq | 4.15.0 (current stable, same release as Testcontainers.PostgreSql) | MIT | test only |

None is reciprocal-only or commercial (ADR 0002).

### The rule the RabbitMQ package implements

- **Publishing Host.** Each published wire name gets one route, from the publication and never from a subscription, to a
  fanout exchange named by the wire name, with a durable outbox. The Unit of Work's Outbox then writes the envelope in
  its commit whether or not a subscription in the Host receives it. The Wave 1 consequence "No subscription, nothing
  written" holds only for a type with no broker route.
- **Subscribing Host, endpoint to subscription.** One durable broker queue per Host, named by
  `WolverineOptions.ServiceName`, bound to the exchange of each wire name the Host subscribes to and does not publish
  itself, with `UseDurableInbox()`. The pre-built handler checks one thing first: an envelope received at that queue is
  published again inside the Host, carrying every `pouryakit-` header. Every other envelope arrived on a local queue and
  is delivered as it is today. The subscription is the local queue's name, and no broker address maps to a subscription.
- **`ServiceName` is unique per kind of Host across the broker.** Instances of one Host share its queue as competing
  receivers, which is intended. Two different Hosts with the same name would split the queue's messages, and the Kit
  checks no name across Hosts. The Kit documents the rule, and the risk if it is broken. It is a deployment rule because no
  check is possible: on the broker a replica and a foreign Host are indistinguishable. Every instance of a
  Host consumes under the same consumer tag, each Host has its own database, so Wolverine's node registry never sees
  another Host's nodes, and the broker compares only its own queue arguments when a queue is declared again.
- **Envelope identity.** The store keeps `MessageIdentity.IdOnly`; `IdAndDestination` is not required, because no
  Host listens to one envelope on two endpoints. The broker envelope has one id. Each local copy has an id of its own.
  The Receipt's identity is the Domain Event id header, or the Snapshot's derived identity, and never an envelope id.
- **Dead letters.** `DisableDeadLetterQueueing()`, so that dead letters stay in Wolverine's storage.
- **Dead letters at the Host's broker queue (inferred, not tested).** A failure before or during the fan-out is
  dead-lettered at `rabbitmq://queue/{ServiceName}`, which names no subscription. Examples are an unknown wire name, a
  body that cannot be read into the contract type, or a store error while the copies are written. Until the strict wire reading
  and reserved names amendment below, a body of another shape was read, with default values, and only a malformed body or
  a token of the wrong JSON type failed; since then a body that lacks a member the contract requires, or carries null
  for a non-nullable one, fails too. The reasons:
  - the global failure policy (`Rmq.Kit/BrokerTopology.cs:92`) and `UnknownMessageBehavior.DeadLetterQueue` apply to
    the broker listener too;
  - a failure while the copies are flushed moves the envelope to the error queue
    (`src/Wolverine/Runtime/MessageSucceededContinuation.cs:28-40` at `V6.46.0`).

  Dead-letter triage, and `PouryaKit.Testing`'s lookup, must cover that address.

Registration as it ran, abridged from `Rmq.Kit/BrokerTopology.cs` and `Rmq.Kit/SubscriptionDelivery.cs`:

```csharp
// Publishing Host: from each publication.
wolverine.UseRabbitMq(broker).AutoProvision().DisableDeadLetterQueueing();
wolverine.PublishMessage(type).ToRabbitExchange(wireName, e => e.ExchangeType = ExchangeType.Fanout).UseDurableOutbox();

// Subscribing Host: one queue per Host, then ADR 0006's local routes.
var rabbit = wolverine.UseRabbitMq(broker).AutoProvision().DisableDeadLetterQueueing();
foreach (var wireName in subscribedWireNames) rabbit.BindExchange(wireName, ExchangeType.Fanout).ToQueue(serviceName);
wolverine.ListenToRabbitQueue(serviceName).UseDurableInbox();
foreach (var s in subscriptions) wolverine.PublishMessage(s.MessageType).ToLocalQueue(s.Name);
foreach (var type in subscribedTypes) wolverine.AddMessageHandler(type, new SubscriptionDelivery(endpoints));

// In SubscriptionDelivery.HandleAsync: an envelope from the Host's broker queue fans out.
var options = new DeliveryOptions();
foreach (var (name, value) in envelope.Headers)
    if (name.StartsWith("pouryakit-", StringComparison.Ordinal)) options.Headers[name] = value;
await context.PublishAsync(envelope.Message!, options);
```

After `PublishAsync`, the Spike also called `context.FlushOutgoingMessagesAsync()` and a test hook. They were there only
to hold the gap open in `RedeliveryTests`, and the Kit needs neither.

These follow from reading the Kit's code at `0e48945`; the Spike used stand-ins and did not change it:

- **Wire names for remote events.** `src/PouryaKit.Messaging.Wolverine/PouryaKitWolverineExtension.cs:16-25` refuses a
  subscription whose Integration Event no Module of the Host publishes, because only a publication declares a wire name.
  A Host that subscribes to a remote event needs a wire name declared without publishing it; the Spike passed it on
  `Subscribe`. It must still be refused at startup when it is missing.
- **The fan-out branch.** `src/PouryaKit.Messaging.Wolverine/SubscriptionDelivery.cs:22` reads the subscription from
  `Destination.Host`. The branch for the Host's broker queue goes before that line.
- **The startup check.** `src/PouryaKit.Messaging.Wolverine/StartupCheck.cs:29-42` checks only the local queues. It
  should also require a durable Inbox on the broker listener. *Done ("Implemented", below); the file is
  `DurableQueuesCheck.cs` since 2026-10-08, which moved its Outbox part to the composition check `OutboxCheck`
  and keeps the queue and broker-listener checks at the Host's `StartedAsync` (its own `IHostedLifecycleService`), once Wolverine has started.*
- **A wire name a Host both publishes and subscribes to** must not be bound to that Host's broker queue. Its local
  routes already deliver it. Bound, it would come back from the broker and be delivered twice, and only the Receipt
  would stop it. The fan-out's `PublishAsync` would also reach the exchange again, through the Host's own broker
  route. This is inferred and was not tested. *Tested (2026-10-07):
  `RabbitMqDeliveryTests.A_publication_whose_only_subscription_is_in_another_host_is_written_in_its_commit_and_applied_there_once`
  shows such a wire name unbound from the Host's own queue ("Implemented", below).*

### Invariants for permanent tests

All rows run against RabbitMQ and PostgreSQL in Testcontainers, with separate publishing and subscribing Hosts.

| Invariant | Seam |
|---|---|
| A publication whose only subscriptions are in another Host is written to the broker route in the Unit of Work's commit, and each remote subscription applies it once | Gateway |
| A failing subscription is retried with the stored retry and dead-lettered at `local://{subscription}/`. Another subscription to the same wire name in that Host commits its effect and Receipt once, without waiting for it | Gateway |
| A broker envelope handed out again, including after a kill between the fan-out and its handled mark, fans out again, and the Receipts keep each effect once | Gateway |
| A delivery waiting for its retry survives a kill and restart of the subscribing Host, and a publication made while that Host is down is delivered after it starts | Gateway |
| Two Hosts with different `WolverineOptions.ServiceName` values that subscribe to one wire name each receive every message | Gateway |
| The store keeps `IdOnly`, and both Receipts of one message carry the Domain Event id across the broker and the fan-out | Gateway |
| Every broker listener has a durable Inbox, and no broker dead-letter queue is declared | Gateway |

### Considered option: A, one broker queue per subscription

A was given its fair configuration: `IdAndDestination`, `DisableDeadLetterQueueing()`, a durable Inbox on every
listener and an endpoint map from each subscription's queue. Under it, A kept every point above. It is rejected for
two observed reasons:

- it needs `IdAndDestination`, which changes the store's identity and keys. Under `IdOnly` it silently drops one
  subscription's copy;
- dead letters and the subscription lookup move to broker addresses, away from what Wave 1 built and tested.

Queue naming is not a reason. With queues named `{ServiceName}.{subscription}`, A gives each Host every message, and B
depends on a unique `ServiceName` in the same way.

### Limitations

- **A message published before any subscribing Host has declared its queue is lost** under either topology, because the
  publishing Host only knows its exchange. A subscribing Host must be deployed, and its queue declared, before the first
  publication it must receive. Another option would be for the publishing Host to publish with an alternate exchange, or
  for queues to be provisioned outside the Hosts. Neither was tested. The RabbitMQ package decides which, and documents it.
- **Same process, single node.** Both Hosts ran in one test process, each in Durability mode `Solo`. Several instances
  of one Host sharing its broker queue and store (`Balanced`) were not run. The local queues behave as in Wave 1;
  the broker queue would get competing receivers. Real separate processes are the RabbitMQ package's proof.
- **Not measured.** The cost of the extra stored envelope and the extra local hop was not measured. Operation Context
  and Snapshot headers were copied by prefix but asserted only for the Domain Event id.
- **Stand-ins.** The Spike's Receipt, Unit of Work and handler interfaces are stand-ins for the Kit's. Its Receipt step
  matches `src/PouryaKit.Messaging.Wolverine/Deliver.cs`, but it has no Operation, dispatcher or nested commands.

### Implemented

Implemented as the rule above states, in `PouryaKit.Messaging.Wolverine.RabbitMq`
(`UsePouryaKitRabbitMq(connectionName, serviceName)`, the service name required). `RabbitMqDeliveryTests` proves each
row of the invariants table against RabbitMQ 4.3.6 and PostgreSQL 18.6, one test with the Proving Sample's
`Inventory.Host` as an OS process. What the Spike left open:

- **The lost first publication: a deployment rule.** A Host that receives a wire name is started, so its queue is
  declared and bound, before the first publication it must receive: on its first deployment, and on the deployment
  that adds its first subscription to that wire name. The Kit adds nothing for it. An alternate exchange still needs a
  queue someone declares and drains, and queues declared by the publishing Host would make it know its subscribers.
- **Remote wire names.** A receiving Host's composition declares them with `Receive<T>(wireName)` and
  `ReceiveSnapshot<T>(wireName)`; a received Snapshot goes through the version gate. A subscription with neither a
  publication nor such a declaration still fails the start.
- **Tested where the amendment inferred.** A failure before the fan-out is dead-lettered at
  `rabbitmq://queue/{ServiceName}`
  (`RabbitMqDeliveryTests.A_message_the_host_cannot_fan_out_is_dead_lettered_in_its_store_at_its_broker_queue`).
  Wolverine stores the copies before it marks the broker envelope handled, and a Host killed between the two fans it out
  again with each effect once; the test holds the handled mark on a database lock
  (`RabbitMqDeliveryTests.A_host_killed_between_storing_the_copies_and_marking_the_broker_envelope_handled_fans_out_again_and_applies_each_effect_once`).
  A wire name a Host both publishes and subscribes to is not bound to its own queue
  (`RabbitMqDeliveryTests.A_publication_whose_only_subscription_is_in_another_host_is_written_in_its_commit_and_applied_there_once`).
- **The startup check** reads Wolverine's own description of each broker listener (`IBrokerEndpoint`, `EndpointMode`,
  `Endpoint.DeadLetterStorage`), so it names no RabbitMQ type.

These rules also hold:

- **The fan-out never sends a copy round again.** It sends each copy with `EndpointFor` to the local queue of each
  subscription to the type, never through the type's routes, which include the type's exchange when the Host publishes
  it. An envelope that reached the queue through a binding the Kit never makes, such as one left by an earlier
  deployment that received a wire name the Host now publishes, then reaches the subscriptions once more and their
  Receipts stop it; routed, it would go back to its exchange and round the broker for ever, each time as a new envelope
  no Receipt recognises
  (`RabbitMqDeliveryTests.A_publication_that_comes_back_through_a_binding_the_kit_never_makes_is_applied_once_and_never_sent_round_again`).
  S2's rejection of `EndpointFor` above does not apply here. There, an EF Core outbox stored its envelope `Outgoing`,
  owner 0, which recovery never hands out. Here the copy is sent from the handler's own context and its durable local
  queue stores it as an incoming delivery owned by the node (`DurableLocalQueue.StoreAndForwardAsync`, which calls
  `PrepareForIncomingPersistence`, at `V6.46.0`). A copy in flight when its Host died is handed out again after the
  restart (`RabbitMqDeliveryTests.A_copy_its_host_was_delivering_when_it_was_killed_is_delivered_after_it_restarts`,
  red when local queues keep what they receive in memory only).
- **The Kit never removes a binding.** A deployment that stops receiving a wire name removes its binding from the
  Host's queue by hand. Left in place, a message of a wire name the Host no longer knows is dead-lettered at the queue,
  and one the Host now publishes itself is stopped by its subscriptions' Receipts.
- **Routing collisions include published types,** which a broker routes to their exchanges: a v2 published under its
  v1's full name is refused at start in a Host that only publishes them
  (`IntegrationEventVersionTests.Startup_fails_when_wolverine_would_send_one_message_type_wherever_another_published_type_goes`).

What the amendment inferred about the Host's broker queue was wrong: a store error while the copies are written
never reached the error queue. A copy sent from the handler's own context only joins that context's outgoing
envelopes. `MessageContext.FlushOutgoingMessagesAsync`, which `MessageSucceededContinuation` runs before it marks the
envelope handled, catches each envelope's failure, logs that the envelope was discarded and does not rethrow, so the
continuation's catch covers only the handled mark (`src/Wolverine/Runtime/MessageContext.cs` at `V6.46.0`, read
decompiled). A copy the store refused was dropped, and the broker envelope marked handled all the same. What holds
now: the fan-out sends the copies through a `MessageBus` of its own, enlisted in no transaction, so each `SendAsync`
returns only once its durable local queue stored the copy, owned by the node as before
(`DurableLocalQueue.StoreAndForwardAsync`, the write retried a few times by Wolverine), and a store error throws. The
broker envelope is then retried, dead-lettered at `rabbitmq://queue/{ServiceName}`, and its replay fans it out again
(`RabbitMqDeliveryTests.A_copy_the_store_refuses_leaves_the_broker_envelope_unhandled_dead_lettered_at_the_host_queue_and_its_replay_applies_each_effect_once`,
red with the copies sent from the handler's context). The fan-out bullet above, which sends the copy from the handler's
own context, is superseded by this; the two kill tests named above pass unchanged.

## Amendment (2026-10-07: Integration Event versions)

The decision above stands. One wire name is one version of one contract type, and a new version is a new type under a
new wire name, published beside the old one during a transition.

- **Wolverine's type route matches by name.** In WolverineFx 6.46.0, `PublishMessage(type)` routes every message whose
  wire name, name or full name equals the routed type's full name, ignoring case (`Subscription.Matches`,
  `RoutingScope.Type`; read from the decompiled assembly). A v2 published under its v1's full name would reach every v1
  subscription; with the Kit's check removed, a probe routed it to the v1 subscription's queue, `local://tests.routed/`.
  The Host refuses such a collision at start (`Subscriptions.RoutingCollisions`, run by the extension before wire names
  are declared), pinned by
  `IntegrationEventVersionTests.Startup_fails_when_wolverine_would_route_one_message_type_to_the_subscriptions_of_another`.
  Each version otherwise reaches only its own subscriptions, each with its own Receipt of the shared identity
  (`IntegrationEventVersionTests.One_operation_publishes_both_versions_and_each_subscription_applies_only_its_own_with_its_receipt_in_one_commit`).

## Amendment (2026-10-07: delayed commands)

The decision above stands. A fourth kind of message follows the same shape: a command a Module schedules
to run later (`IDelayedCommands`), as a continuation of the scheduling Operation. ADR 0004's
commit sequence is unchanged: the Unit of Work writes the command in its Outbox step, in the same transaction.

- **One route and queue per declared command.** `AddPouryaKitDelayedCommand<TCommand>(name, module)` gives the command
  `PublishMessage(commandType).ToLocalQueue(name)` and one pre-built handler, `DelayedCommandDelivery`, registered with
  `AddMessageHandler`, so the command may stay internal. Its deliveries share the Kit's stored retries, dead letters and
  startup queue check; a delayed command's name may not be a subscription's
  (`src/PouryaKit.Messaging.Wolverine/DelayedCommandExtension.cs`).
- **When it runs is Wolverine's.** The Outbox publishes it with `DeliveryOptions.ScheduledTime`, so the store keeps
  the envelope `Scheduled` and owned by no node until its scheduled-job poll finds it due against the system clock;
  any running Host runs it, so the death of the scheduling Host loses nothing
  (`DelayedCommandTests.An_expiry_scheduled_by_a_host_that_dies_before_it_is_due_runs_once_after_a_host_starts_with_the_scheduling_context`).
  The Kit adds no scheduler of its own.
- **Its Receipt.** One envelope is stored per scheduled command, and Wolverine keeps its id through promotion, stored
  retries, recovery and replay, so the Receipt is the declared name and the envelope's id, in
  `pouryakit_receipts`, whose format is unchanged. Each attempt runs the internal `RunDelayedCommand` through the
  dispatcher, which records the Receipt and then sends the scheduled command nested, so the command's authorizers and
  validator run, and its effect and Receipt commit together
  (`DelayedCommandTests.A_delayed_command_handed_out_again_after_it_ran_finds_its_receipt_and_applies_nothing_more`).
- **The stale-time rule does not hold for it.** The scheduled-job poll promotes the row with the node as its owner
  and keeps its insert time, which `InboxStaleTime` counts from (`jasperfx/wolverine` tag `V6.46.0`,
  `src/Persistence/Wolverine.Postgresql/PostgresqlMessageStore.cs:132-137`;
  `src/Persistence/Wolverine.RDBMS/Durability/BumpStaleIncomingEnvelopesOperation.cs`). A delayed command due later
  than the stale time after it was scheduled is therefore stale from its first attempt, and an attempt still running
  at a recovery poll is handed out again, concurrently, as the persisted retry and stale time amendment describes for a late retry; its
  Receipt keeps the database effect to one. No test pins this for delayed commands: the tests' delays are shorter than
  the stale time.
  *Pinned on 2026-10-10: `RecurringCommandTests.A_delayed_command_and_an_occurrence_due_after_the_stale_time_and_handed_out_again_while_they_run_apply_one_effect_each_and_store_one_next_occurrence`
  holds a delayed command and a recurring occurrence, each due about four times the stale time after it was stored,
  after its Receipt until a copy handed out by a recovery poll waits on it: each applies one effect, and the occurrence
  stores one next occurrence. Occurrences of the same runs that were not held, due two to four times the stale time
  after they were stored, each ran once, so a copy is handed out only when a recovery poll comes while an attempt runs.*
- **Its stored names.** `WireNaming` names no command, so Wolverine names it by its full CLR name. Renaming or moving the
  type strands the commands stored before it, which are dead-lettered as unknown messages, as a removed subscription's
  type is. The delivery reads the declaration from the queue the envelope was stored on, as `SubscriptionDelivery`
  reads a subscription, so one stored under a name no longer declared fails with `UnknownDelayedCommandException`
  and is dead-lettered rather than run again under the new name's Receipt key
  (`DelayedCommandTests.A_delayed_command_stored_under_a_name_no_longer_declared_is_dead_lettered_and_never_run`).
  The name follows the subscription name's rule, reserved queues included.
  *Pinned on 2026-10-10: a stored command whose type no declaration names any more is dead-lettered as
  an unknown message, read before from Wolverine's source, is now a live test.
  `DelayedCommandTests.A_delayed_command_whose_type_no_declaration_names_any_more_is_dead_lettered_at_once_and_never_run`
  stores a delayed command from one Host, then starts another on the same database that declares nothing for its
  type: the first time it is handed out, the `IMissingHandler` that `UnknownMessageBehavior.DeadLetterQueue` registers
  moves it to the dead letters, with `Wolverine.Runtime.Interop.UnknownMessageTypeNameException` and the message
  "Unknown message type: '<full CLR name>'", its attempts 0, no incoming row left and nothing run. Red with that
  handler removed: no dead letter within 15 s.*

## Amendment (2026-10-07: recurring commands)

The decision above stands. Independent recurring work follows the delayed command's
shape, with no scheduler of its own: no Quartz, and not Wolverine's own recurring-message agent (`opts.Schedules`),
which publishes each occurrence from its own loop, outside any Unit of Work, so an occurrence could not commit with the
effect of the one before it.

- **A declaration of its own, in the same registry.** `AddPouryaKitRecurringCommand<TCommand>(name, systemActor,
  occurrence, module)` records a delayed command with a `Recurrence`: the system Actor's name and the factory of each
  occurrence's command. Its route, queue, pre-built handler, retries, dead letters and startup queue check are the
  delayed command's (`src/PouryaKit.Messaging.Wolverine/DelayedCommandDeclarations.cs`). Its cadence,
  `PouryaKitRecurringCommandOptions.Every`, is named options validated when the Host starts.
- **Its own Operation.** `DelayedCommandDelivery` begins an occurrence's attempt with a new `OperationContext` of
  `Actor.System(systemActor)`, no tenant and a new correlation, never `ContinuedOperationContext`, so it continues
  nothing, is never a trusted continuation and restores no trace parent (ADR 0009)
  (`RecurringCommandTests.ReconcileStock_runs_under_its_system_actor_and_the_occurrence_a_dying_host_stored_runs_after_a_restart`).
- **The next occurrence commits with this one.** `RunDelayedCommandHandler` records the occurrence's Receipt, stores
  the next occurrence through `IDelayedCommandOutbox`, then sends the command nested, so the next occurrence, the
  effect and the Receipt share one transaction; a failed attempt leaves none of the three, and a dead-lettered one
  stores no next occurrence. Occurrences fall on whole multiples of the cadence from the Unix epoch, and the next is
  the first after both the occurrence and the system clock's now, so missed occurrences are skipped
  (`RecurringCommandTests.An_occurrence_delivered_again_finds_its_receipt_and_neither_reconciles_again_nor_stores_another_occurrence`,
  which compares the stored occurrence's and the reconciliation's commit timestamps).
  *Changed (2026-10-08): a dead-lettered occurrence stopped the
  schedule until a replay or a Host start, against the Capability's purpose (plan I.3, the durable replacement for
  in-memory Quartz stores) and the Glossary's "every occurrence of a cadence". The attempt whose failure sets an
  occurrence aside now stores the next occurrence after now in an Operation of its own, unless one waits, through
  `RecurringCommandStart`'s query and Operation; another chain stored meanwhile meets this one at an occurrence both
  reach, whose Receipt keeps one. The attempt is the one numbered beyond `PouryaKitMessagingOptions.RetryDelays`:
  Wolverine counts an attempt before it runs the handler, then takes the retry policy's slot of that number, one
  stored retry per delay and then the error queue, which every later number goes to as well (`jasperfx/wolverine` tag
  `V6.46.0`, commit `1f22835a2b3e32dbf4b75c7a5273622ee74a4929`, `src/Wolverine/Runtime/Handlers/Executor.cs:235-273`,
  `src/Wolverine/ErrorHandling/PolicyExpression.cs:243-248` and `:321-338`, `src/Wolverine/ErrorHandling/FailureRule.cs:31-67`).
  An earlier failed attempt stores nothing, since its retry may commit the next occurrence itself
  (`RecurringCommandTests.An_occurrence_set_aside_as_a_dead_letter_stores_the_next_occurrence_which_runs`,
  `RecurringCommandTests.An_occurrence_that_succeeds_on_its_last_attempt_leaves_only_the_next_occurrence_its_commit_stored`).*
- **Its Receipt is the occurrence, not the envelope.** Wolverine clears `Envelope.ScheduledTime` when the occurrence
  comes due (`jasperfx/wolverine` tag `V6.46.0`, commit `1f22835a2b3e32dbf4b75c7a5273622ee74a4929`,
  `src/Wolverine/Runtime/Recurring/RecurringMessage.cs:24-30`, the documentation of `OccurrenceHeaderKey`), so the
  Outbox writes the occurrence's time in the `DelayedCommandHeaders.Occurrence` header, on an occurrence only
  (`IDelayedCommandOutbox.AddOccurrence`), and the Receipt of a recurring command is its name and a key derived from
  that time, in `pouryakit_receipts`, whose format is unchanged. An occurrence that finds its Receipt applies nothing
  and stores no next occurrence.
- **The header marks the kind.** No delayed command carries it, so the delivery refuses, before it chooses the
  authority or the Receipt, a delivery stored as the other kind of its name: a delayed command stored before the name
  was declared recurring, or an occurrence stored before it was declared delayed. It throws
  `UnknownDelayedCommandException`, so the delivery is dead-lettered and never runs on the other kind's authority
  (`RecurringCommandTests.A_delayed_command_stored_before_its_name_was_declared_recurring_is_dead_lettered_and_never_run`).
- **A Host start.** The internal `RecurringCommandStart`, after every hosted service started, asks Wolverine's
  `IMessageStore.ScheduledMessages` for a `Scheduled` occurrence on the declaration's queue and, when none waits,
  stores the first occurrence after now as an Operation of the system Actor in the Module. An occurrence taken from the
  store and still running is not `Scheduled`, so a Host starting then, or two Hosts starting together, can store a
  second chain; both fall on the same occurrence times, the first one both reach is applied once, and the chain whose
  delivery found the Receipt ends
  (`RecurringCommandTests.A_starting_host_stores_the_first_occurrence_only_when_none_waits`,
  `RecurringCommandTests.A_second_chain_a_starting_host_stores_while_an_occurrence_runs_applies_nothing_and_ends`).
  The guarantee is one database effect per declaration and occurrence time; the Kit claims no exactly-once run and
  nothing for an effect outside the database.

## Amendment (2026-10-07: strict wire reading and reserved names)

The decision above stands. Wolverine read every envelope body with the Kit's lenient JSON (`UsePouryaKitJson`), so a
body of another shape under a wire name, such as one a publishing Host writes from a contract of its own, was read into
the receiving contract type with default values and the handler ran. This closes that gap where messages are
read, and moves two broker rules to registration or to deployment.

- **Envelopes are read strictly.** `UsePouryaKitWolverine` gives Wolverine's System.Text.Json serializer
  `UsePouryaKitWireJson()`: the Kit's one representation with .NET 10's `RespectRequiredConstructorParameters` and
  `RespectNullableAnnotations` on. One factory, one stack: HTTP bodies, cached answers and recorded responses
  keep the lenient reading, pinned by `SharedJsonTests.Only_messaging_wolverine_reads_strictly_and_web_cqrs_and_caching_keep_the_lenient_reading`
  and `WebTests.A_body_without_a_member_binds_it_as_its_default_and_validation_answers_400_for_it`.
- **The rule.** A body is read only when it carries every member the contract requires, each constructor parameter
  without a default value and each `required` member, and none of them is null where the contract's nullable annotations
  forbid it. System.Text.Json sees the annotations of properties and constructor parameters compiled with nullable
  reference types enabled, not of collection elements or generic arguments, and a contract whose members are settable or
  `init` properties without `required` requires none. The guarantee therefore holds for the positional records with
  nullable reference types the Kit's Contracts use. The same options write: serializing a null into a non-nullable
  member throws `JsonException`, tested at the Domain seam
  (`WireJsonTests.The_wire_options_refuse_to_write_null_into_a_non_nullable_member`, red with `RespectNullableAnnotations`
  off). In a Host that serialization happens when the Unit of Work writes the message to the Outbox, because Wolverine's
  EF Core outbox serializes an envelope as it adds it to the transaction (`EfCoreEnvelopeTransaction.PersistOutgoingAsync`,
  `OutgoingMessage`, read decompiled from WolverineFx.EntityFrameworkCore 6.46.0), so the publishing delivery fails and
  nothing commits. That path through the commit is read from the decompiled code and not proven by a test.
  *Corrected on 2026-10-08: `OutgoingMessage` belongs to
  the branch for a DbContext with Wolverine's mappings (`IsWolverineEnabled()`), which the Kit never takes (ADR 0004). The
  path is now proven: `StrictWireWritingTests.A_notification_handlers_integration_event_commits_with_its_effect_only_when_the_wire_options_can_write_it`,
  against PostgreSQL, has a Notification Handler write a note and publish an Integration Event with null in a
  non-nullable member. The exception's stack shows `WolverineOutbox.WriteAsync`, called by `EfUnitOfWork.RunAsync` before
  the save, then `MessageBus.PersistOrSendAsync`, `EfCoreEnvelopeTransaction.PersistIncomingAsync` on its unmapped branch,
  `DatabasePersistence.BuildIncomingStorageCommand`, `EnvelopeSerializer.Serialize` and `Envelope.Data`, where
  `SystemTextJsonSerializer.Write` throws the `JsonException` that Wolverine wraps in a `WolverineSerializationException`.
  The delivery is retried and dead-lettered with that exception, and its note, its Receipt, its publication record and
  the envelope roll back, with no receiver effect; the same handler with the text commits each. Red with Wolverine writing
  through `UsePouryaKitJson`: the null was written and delivered. An envelope routed to another Host goes through
  `PersistOutgoingAsync`'s unmapped branch and `DatabasePersistence.BuildOutgoingStorageCommand`, which calls the same
  `EnvelopeSerializer.Serialize`, read decompiled from WolverineFx.RDBMS 6.46.0.*
- **Where the failure lands** (decompiled from WolverineFx 6.46.0 and WolverineFx.RDBMS 6.46.0):
  `HandlerPipeline.TryDeserializeEnvelope` catches the `JsonException` thrown by `SystemTextJsonSerializer.ReadFromData`
  and returns `MoveToErrorQueue`, so the failure policy's retries never apply. `MessageContext.MoveToDeadLetterQueueAsync`
  finds no native dead-letter queue (`DisableDeadLetterQueueing()`) and calls `IMessageInbox.MoveToDeadLetterStorageAsync`,
  which deletes the incoming row and writes a `wolverine_dead_letters` row with the exception's type and message
  (`DatabasePersistence.ConfigureDeadLetterCommands`). Across a broker the body is read at the Host's queue, before the
  fan-out, so the dead letter is received at `rabbitmq://queue/{ServiceName}` and no subscription receives a copy:
  `StrictWireReadingTests.A_body_without_a_member_its_contract_requires_or_with_null_for_one_is_dead_lettered_at_the_host_queue_with_the_error_and_never_handled`,
  two Hosts on RabbitMQ, red with the lenient reader, under which the handler ran with a null name. Inside one Host the
  first attempt of a local delivery is handed the published instance; a stored retry, a recovered delivery and a replay
  are read strictly, so a Domain Event or delayed command stored by a build whose type has since gained a required member
  is dead-lettered instead of run with a default.
  *Corrected on 2026-10-10 (read decompiled from WolverineFx 6.48.3, the pinned version): a Kit Host does
  not take the branch described above, which finds no native dead-letter queue and runs Wolverine's
  `IDeadLetterInterceptor`s before `IMessageInbox.MoveToDeadLetterStorageAsync`. Every queue a Kit Host receives a
  delivery on stores what it receives (its local queues through `UseDurableLocalQueues()`, its RabbitMQ Host queue
  through `UseDurableInbox()`), and a durable listener hands the pipeline its `DurableReceiver`, which reports a
  native dead-letter queue (`NativeDeadLetterQueueEnabled` is true). `MessageContext.MoveToDeadLetterQueueAsync`
  therefore calls `DurableReceiver.MoveToErrorsAsync`, which writes the same `wolverine_dead_letters` row through
  `IMessageInbox.MoveToDeadLetterStorageAsync` unless the endpoint builds a dead-letter sender. A local queue builds
  none (`Endpoint.TryBuildDeadLetterSender`), and the RabbitMQ Host queue none because `DisableDeadLetterQueueing()`
  (`RabbitMqExtension.cs`) turns the broker's dead-letter queue off (`RabbitMqQueue.TryBuildDeadLetterSender`, read in
  WolverineFx.RabbitMQ 6.48.3, the pinned version). The outcome above holds on both; no `IDeadLetterInterceptor` runs for a Kit Host, and
  the Kit registers none.*
- **Unknown members are skipped.** A member the body carries and the
  contract lacks is ignored (`JsonUnmappedMemberHandling.Skip`, set explicitly). The versioning rule (the Integration Event versions amendment) makes "a change
  receivers could not read" a new version, which presumes changes they can read; skipping is what makes adding a member
  one of them, so a publisher can add one while receiving Hosts, or a stored delivery's next build, run without it during
  a rollout. System.Text.Json, Wolverine and the tolerant readers of messaging platforms skip too. An unknown member never
  leaves a default in the receiver, since every member the receiver reads is checked: the required and nullable rules
  alone close the silent-default class. The trade-off: a member a publisher adds stays invisible to a receiver until its
  contract has it, and a body of another shape that happens to carry every member the receiving contract requires is
  read. `JsonUnmappedMemberHandling.Disallow` would refuse it, and would dead-letter every message of a rollout that adds
  a member. Pinned by `StrictWireReadingTests.A_body_with_a_member_its_contract_lacks_is_handled_with_the_members_it_has`,
  red with `Disallow`.
- **Reserved broker names are refused at registration.** RabbitMQ 4.3.6 refuses to declare a queue or exchange whose name
  begins with `amq.` (403 `ACCESS_REFUSED`, "contains reserved prefix 'amq.*'"), so
  such a wire name or service name failed late, when the Host started, at the broker. `MessageNames.IsValid` now refuses
  the prefix for every name it checks: wire names in `Publish`, `PublishSnapshot`, `Receive` and `ReceiveSnapshot`,
  subscription and delayed command names, and the service name of `UsePouryaKitRabbitMq`, each with the rule in the
  message. The broker refuses only the lower-case prefix; the Kit refuses it in any case, because it compares names
  ignoring case and Wolverine's own exchange check lower-cases the name (`RabbitMqExchange.CheckAsync`, WolverineFx.RabbitMQ
  6.46.0). Pinned by `IntegrationEventWiringTests.A_composition_root_fails_on` and
  `IntegrationEventWiringTests.A_service_name_rabbitmq_reserves_is_refused_when_the_host_joins_the_broker`.
- **Service-name uniqueness is a deployment rule,** with no check, for the reasons in the `ServiceName` bullet of the
  RabbitMQ topology amendment.

## Amendment (2026-10-07: a declaration's Module comes from the builder it is made on)

The decision above stands. The plan sketched the declarations without a Module argument (plan section D:
`module.Publish<ProductRenamedV1>("…")`); the Wave 1 amendment above added one because neither Messaging package may
reference Modularity. This returns to the sketch through a seam both may reference.

- **The Module is the builder's.** `Publish`, `Subscribe`, `PublishSnapshot`, `AddPouryaKitNotificationHandlers`,
  `AddPouryaKitDelayedCommand` and `AddPouryaKitRecurringCommand` lost their `Type? module = null` parameter. Made on a
  Module's builder, which implements Cqrs's `IModuleServices`, a declaration belongs to that Module; made on the Host's
  own services, to the application, as in an application without Modules. `AddPouryaKitNotificationHandlers` takes
  `params Assembly[]` like its siblings and, with none, scans the Module's assembly (ADR 0001 amendment of this date).
- **One explicit form, for the Host's composition.** Each declaration keeps an overload with a required `Type module`,
  for a declaration the Host makes on a Module's behalf, such as a test's extra subscription; made on another Module's
  builder it throws while the Host is composed. Both forms share one implementation. `Receive` and `ReceiveSnapshot`
  stay the Host's.
- **None by omission.** A declaration that belongs to no Module in a Host that composed Modules, or that names a Module
  the Host never composed, would have failed only at its first use: the first publish, commit, delivery or scheduling.
  A composition check now fails the Host when its container is built, one line per declaration, naming its form on the
  Module's builder and its form that names the Module. Every Module `AddPouryaKitModules` adds counts as composed. The
  commit-time refusal of a Snapshot declared with a Module in an application without Modules stays, only as the backstop
  of a Host that does not call `AddPouryaKitCompositionChecks`; in one that does, the declaration fails the build.
- Proven by `CompositionCheckTests.A_module_declaration_made_without_its_module_in_a_host_of_modules_fails_the_host_when_built`,
  one case per declaration, each red with the check switched off, by
  `CompositionCheckTests.A_declaration_without_its_module_fails_the_build_of_a_host_whose_modules_make_no_kit_registration`
  and by `CompositionCheckTests.A_declaration_for_a_module_the_host_never_composed_fails_the_host_when_built`, against
  PostgreSQL.

## Amendment (2026-10-08: the routing refusals at the build, and a subscription's Unit of Work)

The decision above stands. Three problems were fixed: the refusals of the Host's routes ran at a moment of
their own, one declaration bound nothing without a word, and one Module's every delivery failed although the Host started.

- **Routes are refused when the Host is built.** The refusals the declarations and names, Integration Event versions and RabbitMQ topology amendments place at the start (a
  subscription whose Integration Event or Snapshot has no wire name, a routing collision, a wire name the process sends
  otherwise or that Wolverine named first, and, from the delayed commands amendment, a delayed command named as a subscription) ran in
  `PouryaKitWolverineExtension` and `DelayedCommandExtension`, when Wolverine's options were first resolved. They read
  only the Host's final declarations, so a built Host that was never started met them inside its first publishing
  Operation. They are now the composition check `RoutingCheck`, registered by `UsePouryaKitWolverine`, and the two
  extensions only route. `WireNaming` declares the Host's wire names in that check, at the build, and only when the Host's
  routes pass, so a Host built and never started keeps them for the process, as one that started and stopped does, even
  when another composition check then fails its build. The Host's checks have three moments: the build, `StartingAsync`
  and Wolverine's `StartedAsync` (`DurableQueuesCheck`). The tests that pinned the start now assert the build and are
  renamed from `Startup_fails_…` to `Building_fails_…`.
- **A received type needs a subscription.** `IWireNames.Received` keeps only the received wire names a subscription of
  the Host handles, so a `Receive` or `ReceiveSnapshot` no subscription handles bound nothing to the Host's queue,
  received nothing and reported nothing. `RoutingCheck` refuses it, naming `Subscribe<T, THandler>`.
- **A subscription needs its Module's Unit of Work.** Every delivery runs `Deliver` as a command in its subscription's
  Module, whose Unit of Work records the Receipt. A Module that handles a command of its own was checked as it was
  composed, but one whose Composition Root only subscribed passed every check, and every delivery then failed at the Unit
  of Work router. The composition check `SubscriptionUnitOfWorkCheck`, registered by `UsePouryaKitWolverine`, fails the
  build for each composed Module with a subscription and no keyed Unit of Work, and, in an application without Modules,
  for the application's subscriptions when no Unit of Work is registered, one line per Module naming its subscriptions
  and `UsePostgres`. A Module that only answers queries needs none.
- Proven by `CompositionCheckTests.A_module_that_subscribes_but_registers_no_unit_of_work_fails_the_host_when_built_naming_use_postgres`,
  `CompositionCheckTests.A_delayed_command_with_a_subscriptions_name_fails_the_host_when_built`,
  `IntegrationEventWiringTests.Building_fails_on_a_type_received_from_another_host_that_no_subscription_handles_naming_subscribe`
  and the renamed `Building_fails_…` tests of `IntegrationEventTests`, `IntegrationEventVersionTests` and
  `IntegrationEventWiringTests`, each red with its check or line switched off.

## Amendment (2026-10-08: the `PouryaKit` prefix on two registrations)

The decision above stands. The two registrations of the Kit without the `PouryaKit`
prefix take it before the first publication, with no alias and no forwarder: `UsePostgres<TDbContext>`
(`PouryaKit.Persistence.Postgres`) is now `UsePouryaKitPostgres<TDbContext>`, and `MapUniqueConstraint<TDbContext>`
(`PouryaKit.Persistence.EFCore`) is now `MapPouryaKitUniqueConstraint<TDbContext>`. Signatures and behaviour are
unchanged. The text above keeps the former names, as it was written: where it says `UsePostgres`, the code says
`UsePouryaKitPostgres`, the line `SubscriptionUnitOfWorkCheck` reports names the new call, and the test the amendment
above cites as `…_naming_use_postgres` is now
`CompositionCheckTests.A_module_that_subscribes_but_registers_no_unit_of_work_fails_the_host_when_built_naming_use_pouryakit_postgres`.

## Amendment (2026-10-10: a member named twice)

The decision above stands, and so does the strict reading of the amendment on strict wire reading.
`UsePouryaKitJson`, which `UsePouryaKitWireJson` builds on, turns .NET 10's `AllowDuplicateProperties` off, so a
body that names one of its contract's members twice, in its own case or another (`name` and `Name`, read ignoring case),
fails to read with a `JsonException` (`Duplicate property 'name' encountered during deserialization of type '…'`)
instead of keeping the last value. Across a broker it is dead-lettered at the Host's queue before the fan-out, as the
strict wire reading amendment describes, and no subscription receives a copy:
`StrictWireReadingTests.A_body_that_names_a_member_of_its_contract_twice_is_dead_lettered_at_the_host_queue_with_the_error_and_never_handled`,
red with the option at its default, under which the handler ran with the last value. A member the contract lacks is
still skipped, once or twice. HTTP bodies, cached answers and recorded responses refuse a member named twice too, since
they share `UsePouryaKitJson`; otherwise their reading stays lenient.

## Amendment (2026-10-10: Receipts and publication records are kept)

The decision above stands: the Kit keeps every Receipt and every
publication record, and its one cleanup removes idempotency keys only.

- **What grows.** `pouryakit_receipts` holds one row per delivery a subscription applied, keyed (subscription, message
  id) with no time column: a Notification's, an Integration Event's, a Snapshot's, a delayed command's and a recurring
  occurrence's. A publishing Module's table also holds one `<wire name>#published` row per Integration Event it
  published, one per Domain Event and type. Nothing removes either, as the Wave 1 amendment's "What a consuming project
  must keep" records: the table grows by one row per delivery and one per publication.
- **Why no retention tied to dead-letter expiry.** A Receipt is what stops a second effect on a redelivery that never
  enters the dead-letter store. A broker envelope whose local copies all committed stays unhandled when its Host dies
  before marking it, and is fanned out again when the Host restarts, where each copy finds its Receipt and applies
  nothing (`RabbitMqDeliveryTests.A_host_killed_between_storing_the_copies_and_marking_the_broker_envelope_handled_fans_out_again_and_applies_each_effect_once`).
  A horizon tied to dead letters does not cover that path. Wolverine's `DurabilitySettings.DeadLetterQueueExpirationEnabled`
  (off by default, 10 days when on, read in WolverineFx 6.48.3) bounds dead letters only. A `#published` row cannot
  age out either: it is the check that one Domain Event yields at most one Integration Event of each type
  (`IntegrationEventTests.A_second_notification_handler_of_one_domain_event_publishing_the_same_type_fails_instead_of_being_dropped`,
  the Wave 1 amendment above).
- **The cleanup stays for idempotency keys.** `IdempotencyKeyCleanup` (`PouryaKit.Persistence.EFCore`) removes expired idempotency keys and touches no Receipt, publication record or Snapshot version.
- **A bounded policy is its own spec.** If one is ever wanted, it first accounts for every redelivery path, pending
  broker envelopes, dead-letter replay and duplicate chains, before it deletes any deduplication state. Adding a time
  column is then a migration in every Module that keeps Receipts.

## Amendment (2026-10-17: tenants at delivery and in the keys)

The decision above stands, and so do the Receipt key (subscription, message id) and the Snapshot version key
(subscription, aggregate key): both are kept as built, with no tenant column and no
migration of any Module's Receipt or Snapshot version table.

- **Why no tenant in the keys.** A Notification's and an Integration Event's message id is its Domain Event's version 7
  Guid, unique whichever tenant raised it. A Snapshot's identity derives from its wire name, its aggregate's key and the
  version (`SubscriptionDelivery`), and its version key holds the aggregate's key, so both are unique across tenants
  exactly when that key is: a single Guid, or a key that includes the tenant.
- **The check that replaces a tenant in the key.** `SnapshotKeyTenancyCheck` (`PouryaKit.Messaging.Wolverine.EFCore`,
  registered by `UsePouryaKitWolverine(connectionName)`) fails the Host's build when a tenant-scoped Module, or a
  tenant-scoped application, declares a Snapshot of an aggregate whose mapped primary key neither includes its
  `ITenantScoped.Tenant` nor is a single Guid, naming the Module, the aggregate and its key. As at commit, a declaration
  reaches only its own Module's aggregates: the check reads the key from the model of the DbContext `AddPouryaKitEfCore`
  records keyed by the Module, as its Unit of Work is keyed, created in its own scope as `MessageStoreDatabaseCheck`
  creates them, and the Snapshot declarations through `ISnapshotMappers.DeclaredAggregates`. A composite key that holds a Guid and no tenant is refused
  too: the whole key is the Snapshot's aggregate key
  (`CompositionCheckTests.A_snapshot_in_a_tenant_scoped_module_of_an_aggregate_whose_key_two_tenants_could_share_fails_the_host_when_built`:
  a shelf keyed by two codes is refused, a folder keyed by a Guid and a docket keyed by its tenant and code are not, and
  Catalog's products, keyed by a Guid, pass in every Host;
  `CompositionCheckTests.A_snapshot_in_a_tenant_scoped_module_is_judged_by_the_aggregates_of_its_own_db_context_only`:
  a tenant-scoped Module's Snapshot of every aggregate is judged by its own DbContext's, never by another Module's).
- **A delivery with no tenant into a tenant-scoped Module.** The delivery step (`DeliverHandler`) refuses a
  Notification, an Integration Event or a Snapshot whose continued Operation Context carries no tenant, in a Module that
  `HostComposition.IsTenantScoped` names, with `TenantErrors.Required`, before its Receipt and its handler, so nothing is
  written, whatever the handler writes with, plain SQL included. The failed Result is thrown as the subscription's
  failure, so the delivery is retried and then set aside as a dead letter whose message names `Tenant.Required`. The
  tenant requirement `AddPouryaKitTenancy` registers covers the Module's own commands and queries, never the Kit's
  `Deliver`, so this step is separate. A delivery that carries a tenant runs under it
  (`TenantDeliveryTests.A_delivery_with_no_tenant_into_a_tenant_scoped_module_writes_nothing_and_is_dead_lettered_naming_the_reason`,
  `TenantDeliveryTests.A_delivery_into_a_tenant_scoped_module_runs_under_the_tenant_it_carries`).
- **A recurring command in a tenant-scoped Module.** An occurrence runs for its system Actor with no tenant (the recurring
  commands amendment), and the Kit runs no recurring command per tenant, so
  `RecurringCommandTenancyCheck` fails the Host's build for such a declaration, naming the command and the Module. A
  delayed command is not refused: it continues the tenant of the Operation that scheduled it
  (`CompositionCheckTests.A_recurring_command_in_a_tenant_scoped_module_fails_the_host_when_built_naming_the_module`).

## Amendment (2026-10-18: a Saga beside the routed queues)

The decision above stands for every subscription. A Saga is the one reaction that runs on a chain Wolverine generates
rather than on the Kit's pre-built handler; the spike recorded its extent on 6.48.3 in
[ADR 0013](0013-a-saga-step-runs-outside-every-operation.md).

- **One queue per saga, and a Kit input type per saga and event.** The handler `AddMessageHandler` registers for a
  subscribed type is that type's handler on every queue, a saga's included (`HandlerGraph.HandlerFor`, read in
  `JasperFx/wolverine` tag `V6.48.3`), so the Kit delivers each Integration Event a saga takes to the saga's own durable
  local queue as `SagaInput<TSaga, TEvent>`, a type of that saga alone. The route that does it also returns the event's
  subscription routes, which a custom route source would otherwise cut off (ADR 0013, item 1 and "Findings").
- **A claim in place of the Receipt.** A step is no delivery through `Deliver`: the Kit claims it, keyed by the saga's
  name, the input's wire name and the event's identity, the identity a Receipt keys by, in the step's own transaction
  and before Wolverine loads the saga. The claims outlive the saga, as Receipts are kept (the Receipts and publication records amendment above).
- **What a step emits is an Integration Event like any other.** The Kit's adapter writes the Operation Context headers
  and the identity header the delivery step reads, so the ordinary subscriptions of a tenant-scoped Module receive it
  as they receive any.
- **An Integration Event from another Host** reaches a saga only once the broker fan-out of the RabbitMQ topology amendment also
  delivers the saga's input type; the next amendment records it.

## Amendment (2026-10-18: a Saga's input from another Host)

The decision above stands. The Messaging package records each Integration Event a Saga takes beside its subscriptions
(`Subscriptions.SagaInputs`), so a Host whose saga takes an event another Host publishes binds that wire name to its broker
queue, and `SubscriptionDelivery` fans the envelope out to the saga's queue as the saga's input, beside each
subscription's copy, by queue and never through the type's routes ([ADR 0013](0013-a-saga-step-runs-outside-every-operation.md),
the package as built amendment).

## Amendment (2026-10-24: the stale-time rule retired on WolverineFx 6.52.0)

The decision above stands, the stored retry of owner decision 1 included. WolverineFx 6.51.0 restarts a row's stale
clock when a node claims it and while a receiver holds it (ADR 0004's WolverineFx 6.52.0 amendment), so the inferences of the
persisted retry and stale time and delayed commands amendments about the insert time no longer describe the store:

- **A late retry, delayed command or recurring occurrence is not stale when it starts.** The scheduled-job poll's
  claim restarts the clock, so none of them is handed out again for having been stored longer ago than the stale time.
  `InboxStaleTimeTests.A_retry_due_after_the_stale_time_is_not_handed_out_again_while_it_runs` holds a retry due a
  second after a 3-second stale time across ten recovery polls and no copy begins; with the claim's restart switched off
  by setting the row's clock back, a copy begins. It replaces
  `InboxStaleTimeTests.A_retry_that_runs_after_the_stale_time_is_handed_out_again_while_it_runs_and_its_receipt_keeps_one_effect`,
  whose copy no longer comes; the waiting-retry test stands as it was.
- **The rule a Host follows changes.** The stale time no longer has to exceed the retry delays plus every attempt's
  run: it stays well above the difference between the Host's clock and the database's, which the release compares, and
  a loaded Host's delay in restarting a held row's clock, which the 5-minute default does. The default delays stay 1 s,
  10 s and 1 min.
- **A concurrent copy still keeps one effect.** A row released while an attempt of it runs, as when its receiver
  misses a restart, is handed out again, and its Receipt keeps the effect to one and a recurring command's next
  occurrence to one. The tests that showed it through the stale sweep now release the held row themselves, as
  `IntegrationEventTests.Concurrent_deliveries_of_one_integration_event_apply_its_effect_once` always did:
  `StreamTests.A_concurrent_duplicate_delivery_waits_for_the_first_receipt_and_never_runs_its_handler` and
  `RecurringCommandTests.A_delayed_command_and_an_occurrence_due_after_the_stale_time_and_handed_out_again_while_they_run_apply_one_effect_each_and_store_one_next_occurrence`.
