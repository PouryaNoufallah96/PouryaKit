---
status: accepted
---

# A Saga step runs on Wolverine's own chain outside every Operation; the Kit routes it, claims it in its transaction and emits for it

Spike #141 (spec #115, "Saga", decision S1) ran Wolverine sagas beside the Kit's delivery step against WolverineFx
6.48.3 and PostgreSQL, through the Kit's own Modules, Outbox, subscriptions and message store. **A Saga is the one
reaction that runs outside an Operation, the exception to ADR 0001 and ADR 0006: its state and handler type is a public
Wolverine `Saga` that Wolverine discovers and whose chain Wolverine generates, and a step runs with no Operation Context
and the Host's authority. The Kit bounds the exception. It routes each Integration Event a saga takes to the saga's own
durable local queue, as a Kit input type of that saga and that event. It claims each step, keyed by the saga's name, the
input's wire name and the event's identity, in the step's own transaction and before Wolverine loads the saga, so a
second delivery of one event applies nothing and a crash between the step's commit and the mark of its envelope applies
no step twice. The claims outlive the saga: the spec's "kept until the saga completes" is read as a floor, not as the
point they are deleted, since deleting them then lets a late copy of the starting event start the saga again. It makes
the saga's identity the event's tenant with its business id, each escaped, so no tenant can name another's saga, and
refuses an event with no tenant before any step. It writes every emission, a timeout's included, through an adapter
that gives it the saga's declared system Actor, the starting event's tenant and correlation, kept in the saga's state,
the step's identity as causation and an identity every delivery of the step repeats; a timeout's own stable identity is
kept in the saga's state by the step that scheduled it, one for each timeout, two of one type
included.** The saga's state is a row of
Wolverine's lightweight saga table in the Kit's message store schema, and a Host with a saga carries the generated chains
in its Release publish through the existing code generation step. A middleware that begins an Operation around a step
was tried: the step can read the context it sets, and stays outside every Unit of Work and the Kit's publisher, so S1's
"no Operation Context on a step" stands. Nothing observed contradicts ADR 0002 to 0005 or ADR 0007 to 0012, or section G.

## Evidence

The Spike code is `spikes/saga/` at commit `629b85a8abefd3bfb62bb7d072e30b9669992eae`, reachable through tag
`spike/saga`. It is not merged. It was run on 2026-10-18 with `dotnet test spikes/saga` (.NET SDK 10.0.402, Microsoft
Testing Platform, xunit.v3 4.0.1, `postgres:18.6` with `track_commit_timestamp=on` and `max_connections=300` in
Testcontainers 4.15.0, Docker 29.8.2, macOS arm64), on the Kit at `2152782c`: 22 of 22 tests passed in about 42 s. Each
test gets a database of its own; tests run one at a time. Spike code: 2,216 lines of C# over 24 files, counted with
`wc -l` at that commit, blank lines and comments included; 704 of them in `SagaSpike.Kit`, what #142 would ship.

Read in source: `JasperFx/wolverine` tag `V6.48.3`, commit `c1eab2a7f04de7350c3b54741ae00351c6c303d1`, and its saga
guide, `docs/guide/durability/sagas.md`. Paths below are into that repository unless they start with `spikes/`.

**Configuration.**

- **The Kit, unchanged, by `ProjectReference`**: `AddPouryaKitModules`, the dispatcher, `UsePouryaKitWolverine("app")`
  of `PouryaKit.Messaging.Wolverine.EFCore` (the message store in schema `wolverine`, one durable local queue per
  subscription, the delivery step `Deliver`, `UnknownMessageBehavior.DeadLetterQueue`, conventional discovery off, the
  stored retry), and `PouryaKit.Testing`'s delivery settings inline in the Host (Solo durability, 100 ms polling,
  retries after 100 ms and 100 ms), since the Host is also published in Release, which carries no `PouryaKit.Testing`.
  Nothing under `src/` or `samples/` changed.
- **`SagaSpike.Kit`, what #142's `Messaging.Wolverine.Sagas` would ship, as a self-contained copy**: `PouryaKitSaga`, the
  base of a saga (its identity, tenant and correlation, and the emission adapter `PublishAsync`); `SagaInput<TSaga,
  TEvent>`, the Kit's input type; `AddSpikeSaga`, the declaration on a Module's builder; `SagaRouteSource`, the Kit's
  route; `SagaStepPolicy` and `SagaStorage`, which open the saga's storage on a transaction the Kit owns and claim the
  step in it; and `SagaSpikeOptions`, the switches the rejected options and red runs use, which a package would not have.
- **Three Modules on the Kit's own seams**: `Orders`, an EF Module whose Notification Handlers publish `OrderPlacedV1`
  and, for one payment Domain Event, both `PaymentReceivedV1` and `PaymentReceivedV2`; `Billing`, a tenant-scoped EF
  Module subscribed to `OrderPlacedV1`, `OrderFulfilledV1` and `OrderCancelledV1`; `Shipping`, which declares the saga
  `FulfilmentSaga` under the name `shipping.fulfilment` with the system Actor `shipping.fulfilment`, started by
  `OrderPlacedV1`, advanced by both payment contracts and timed out by `FulfilmentTimeout`, and publishes what it emits.
  The saga fulfils an order after its last payment and cancels it at its timeout.
- **Events published as the Outbox writes them**: where a test needs one event under two envelope ids, or one order id in
  two tenants, it publishes through `IMessageBus` with the Operation Context headers `WithOperationContext` writes and an
  `IntegrationEventHeaders.DomainEventId`, as a broker's fan-out after a crash or a republish would deliver it again.
  Every other event is published by `Orders` through the Kit's Notification Handler path.
- **Between commit and mark**: `SagaSpikeOptions.AfterCommit`, run by the Kit's storage after the step's commit and
  before the step returns, so before Wolverine flushes what the step stored or marks its envelope handled.
- **Red once.** Each deciding test was run once with its behaviour switched off, by a one-line mutation, and failed; the
  switch is named in its row.

## The items

Every outcome is observed in the test it names unless the row says read or inferred. Tests are in `SagaSpike.Tests`.

| # | Item | Outcome | Test | Red once with |
|---|---|---|---|---|
| 1 | A Saga and a Kit subscription on one message type stay separate | Pass, through the Kit's route. `OrderPlacedV1`, published by Orders' Notification Handler, reaches Billing's subscription, one Receipt, and starts the saga, one row, with no dead letter. One event also starts two sagas beside the subscription (item 8's test). The rejected route, the event routed as itself to a plain Wolverine saga's queue, never starts it: the Kit's pre-built handler of the type runs on that queue too, finds no subscription of that name, and the delivery is set aside with `UnknownSubscriptionException`. Read: `HandlerGraph.HandlerFor` returns a handler registered with `AddMessageHandler` on every queue unless the type's chain has a sticky endpoint for it (`src/Wolverine/Runtime/Handlers/HandlerGraph.cs:167-262`), and a single saga never gets one: a saga chain separates its sagas only for two or more saga types under `MultipleHandlerBehavior.Separated` (`src/Wolverine/Persistence/Sagas/SagaChain.cs:88-122`). The Kit therefore delivers the event as `SagaInput<TSaga, TEvent>`, a type with no pre-built handler, whose chain is that saga's alone | `SeparationTests.A_saga_and_a_kit_subscription_on_one_integration_event_each_receive_it_once`; `SeparationTests.Routed_as_itself_an_integration_event_a_kit_subscription_handles_never_reaches_a_wolverine_saga` | The Kit's route off: the saga never started |
| 2 | Saga state lives in the Kit's message store schema | Pass, with the store in a schema of its own, `kitstore`: the saga's row is in `kitstore.fulfilmentsaga_saga`, Wolverine's lightweight table (id `varchar`, body `jsonb`, version, created, last modified), and the Kit's claims in `kitstore.pouryakit_saga_steps`, and neither table is in any other schema. A step's state, its claim and each envelope it stored have one `pg_xact_commit_timestamp`, read between its commit and its return: the first payment's state and claim, and the completing payment's claim and emission. Read: the table is created at the saga's first step, in the schema of the store's `DatabaseSettings`, and an update matches the version it loaded, else `SagaConcurrencyException` (`src/Persistence/Wolverine.Postgresql/Internals/DatabaseSagaSchema.cs:46-132`) | `StateTests.Saga_state_and_its_step_claims_are_rows_of_the_message_store_schema_committed_with_the_steps_emissions` | The saga's table built from settings of schema `public`: no state in `kitstore` |
| 3 | One advancing event under two envelope ids applies once; two contracts sharing one origin both apply | Pass. A payment delivered under two envelope ids advances the saga once, one claim; Orders' two payment contracts for one Domain Event, one identity, both advance it, one claim each; and the claims outlive the saga: a copy of the starting event delivered after the saga completed starts no saga. The rejected option, the claim keyed by the envelope id alone, advances the saga twice | `DeduplicationTests.One_payment_delivered_under_two_envelope_ids_advances_the_saga_once`; `DeduplicationTests.Two_contracts_published_for_one_payment_both_reach_the_saga`; `DeduplicationTests.A_copy_of_the_starting_event_delivered_after_the_saga_completed_starts_no_saga`; `DeduplicationTests.Keyed_by_its_envelope_id_one_payment_under_two_envelope_ids_advances_the_saga_twice` | No claim: two payments counted. A key without the wire name: the second contract skipped. The claims deleted with the saga's state: the copy started a new saga |
| 4 | The same business id in two tenants is two sagas; a wrong-tenant event changes nothing and emits nothing | Pass. One order id under `tenant-a` and `tenant-b` is two sagas, `tenant-a:<id>` and `tenant-b:<id>`, and a payment of `tenant-b` advances only its own. Each part of the identity is escaped as a URI's data (`SagaStorage.SagaId`, as `HybridQueryCache` escapes each part of its key), so tenant `a:b` with the reference `c` and tenant `a` with the reference `b:c` are two sagas. A payment of `tenant-c` names a saga that does not exist: Wolverine's chain throws its own `UnknownSagaException` before the step, at each of the delivery's three attempts, the delivery is then set aside, and nothing is claimed, changed or emitted; the refusal is Wolverine's, not one the Kit names. An event with no tenant is refused before any step, set aside with `Tenant.Required`, and no saga table or claim exists. The events are published as the Outbox writes them, since Orders' own table keys an order by its id alone | `TenancyTests.One_order_id_in_two_tenants_is_two_sagas_and_a_payment_advances_only_its_own_tenants`; `TenancyTests.Two_tenants_whose_tenant_and_business_id_join_alike_are_two_sagas`; `TenancyTests.A_payment_of_another_tenant_changes_no_saga_and_emits_nothing`; `TenancyTests.An_event_with_no_tenant_is_refused_before_any_step` | The identity without the tenant: the second start collided, and the foreign payment advanced the saga. The parts joined unescaped: one saga for the two tenants, the second start refused. The tenantless refusal off: a saga started |
| 5 | A Saga's emission, a timeout's included, reaches an ordinary subscription in a tenant-scoped Module through the Kit-owned adapter | Pass. The completing payment's `OrderFulfilledV1` reaches Billing under Actor `System` `shipping.fulfilment`, tenant `tenant-a`, the correlation of the Operation that placed the order, not of the one that paid, and the step's identity as causation; Billing's Receipt keys it by the emission's identity. The timeout's `OrderCancelledV1` reaches Billing the same way, with the tenant, the correlation and a stable identity the saga's state kept: when the starting step stores the saga, the Kit keeps in its state, under the timeout's envelope, an identity derived from that step's identity (`SagaStorage.TimeoutId`), and the timeout's step takes that identity, never the envelope's id, for its claim, its own identity and what it emits. The test reads the identity from the saga's state before the timeout fires, finds it differs from the timeout envelope's id, and finds the claim, the causation and Billing's Receipt key derived from it. Each timeout a step schedules keeps an identity of its own: the derivation also takes the timeout's ordinal, its place from 0 among the step's timeouts of that type in the order the step scheduled them, the order of `MessageContext.Outstanding`, a list Wolverine appends each stored envelope to (`src/Wolverine/Runtime/MessageBus.cs:17,73-78,365-369`, read). `ReminderSaga`'s starting step schedules two `ReminderTimeout`s: both apply, under two claims whose identities are the two the saga's state kept. The rejected option, the saga publishing as Wolverine publishes from a saga: Billing sets the delivery aside, "has no pouryakit-actor-kind header" | `EmissionTests.A_completing_steps_emission_reaches_a_tenant_scoped_subscription_under_the_sagas_actor_and_the_starting_events_tenant_and_correlation`; `EmissionTests.A_timeouts_emission_reaches_the_subscription_with_the_identity_tenant_and_correlation_kept_in_the_saga_state`; `EmissionTests.Two_timeouts_of_one_type_from_one_step_each_keep_their_own_identity_and_both_apply`; `EmissionTests.Without_the_kits_adapter_a_sagas_emission_is_set_aside_by_an_ordinary_subscription` | The adapter off: Billing never received it. The adapter without the saga's state: Billing refused the timeout's emission, which had no tenant. The timeout's identity from its envelope: the claim was another identity. The ordinal left out of the timeout's identity: the second reminder was skipped, one of the two applied |
| 6 | A crash between the saga's save and the envelope's mark applies no step twice, or the limit is recorded | Pass. A child process (`dotnet SagaSpike.Host.dll crash-after-commit ...`) prints `COMMITTED` after a payment step's commit and before the step returns, and is killed (SIGKILL): the payment counted, its envelope `Incoming` and owned by the dead node. A Host started on the store recovers and delivers it again, the claim is found, and the payment stays counted once. Killed inside the step, before its commit, the step's claim rolls back with it, and the redelivery applies the step once. Read: lightweight storage commits at the end of the generated method (`src/Persistence/Wolverine.RDBMS/Sagas/DatabaseSagaStorage.cs:34-46`), and the durable receiver marks the envelope handled afterwards, on its own (`src/Wolverine/Runtime/WorkerQueues/DurableReceiver.cs:188-203`); only EF Core's envelope transaction marks it inside the application's transaction (`src/Persistence/Wolverine.RDBMS/IMessageDatabase.cs:103-123`, `src/Persistence/Wolverine.EntityFrameworkCore/Internals/EfCoreEnvelopeTransaction.cs:230`). The limit: the claim stops a committed step from applying twice, and a step killed before its commit runs its code again, so an effect a step had outside its transaction would repeat; the Spike's steps have none, their state, claim, emissions and timeouts all in the one transaction, and that is the rule for saga code. The rejected option, Wolverine's own logical deduplication, loses the payment of a step killed before its commit | `CrashTests.A_step_committed_before_its_process_is_killed_is_not_applied_again_after_restart`; `CrashTests.A_step_whose_process_is_killed_before_it_commits` (`kit`, and `wolverine` for the rejected option) | The restarted Host without the claim: the payment counted twice and the saga completed. The claim on a connection of its own: it survived the kill |
| 7 | The Release publish carries the generated chain, through the existing code generation step | Pass. `codegen write` from the Release build writes the saga's chains into the Host's `Internal/Generated`, with the Kit's storage helper and claim guard in them; a Release restore and publish holds neither `Wolverine.RuntimeCompilation.dll` nor `JasperFx.RuntimeCompiler.dll`; the published Host, in Production (`TypeLoadMode.Static`), starts the saga. The steps are `validate.py`'s `production_publish`, as ADR 0004's #58 amendment records them | `ReleasePublishTests.A_release_publish_carries_the_sagas_generated_chain_and_runs_it_in_production` | No `codegen write`: the published Host did not start, `MissingPreBuiltTypesException`, "4 expected pre-built handler type(s) could not be loaded from the configured ApplicationAssembly 'SagaSpike.Host'" |
| 8 | Whether middleware can restore the Operation Context around a step | Tried, recorded. Under Wolverine's default `ServiceLocationPolicy.NotAllowed` a step cannot take the Kit's `Operation`, an opaque scoped factory, or `IIntegrationEventPublisher`, an internal type: the chain is refused when Wolverine generates it, at the first message (`InvalidServiceLocationException`), and the delivery set aside. With service location allowed, a Wolverine `Before` middleware that begins an Operation on the step's scope with the event's Operation Context headers, as the delivery step does, gives the step that context, Actor, tenant, correlation and Module; without it the step has none. Either way no Unit of Work is active and the Kit's publisher refuses the step, "outside a Notification Handler's delivery". The step's writes commit in the saga's transaction, which no Unit of Work, authorizer, validator or Receipt runs in, so a restored context would claim an Operation the step is not | `OperationContextTests.Middleware_can_begin_an_operation_around_a_step_which_still_runs_outside_the_kits_unit_of_work_and_publisher` (with and without the middleware); `OperationContextTests.Under_wolverines_default_service_location_policy_a_step_cannot_take_the_kits_operation` | Not a guarantee; the two cases are each other's control |

## Rejected options, one honest attempt each

- **Wolverine's saga without the Kit's adapter.** The step publishes as Wolverine publishes from a saga: Wolverine copies
  its own correlation id, tenant id, conversation and saga id onto the envelope (`src/Wolverine/Runtime/MessageBus.cs:402-430`,
  `src/Wolverine/Runtime/MessageContext.cs:1037-1066`, read), none of the Kit's Operation Context headers or an
  Integration Event's identity, so every ordinary subscription sets the delivery aside (item 5). Rejected.
- **Deduplication by the envelope id alone.** A redelivery of one stored envelope repeats its id, so it would cover the
  crash of item 6, but a second delivery of one event does not: the payment is counted twice (item 3). Rejected.
- **Wolverine's own logical deduplication** (6.31+, `MessageDeduplicationMode.CompareByHash`, `Policies.RequireDeduplicationId`
  over the saga's payment chain and `MessageDeduplication.ByMessage` keyed as the Kit keys its claim), the fair
  Wolverine-native configuration of the Kit's key. It is keyed right, but on a relational store it claims on a
  connection of its own before the step and gives the claim back only when the step fails in a live process; only the
  Marten, Polecat and Fisher providers write the claim in the handler's transaction
  (`src/Wolverine/Persistence/Codegen/ChainDeduplicationExtensions.cs:42-113`, read, and the guide's
  `docs/guide/durability/idempotency.md:413-458`). A step killed before its commit leaves the claim, and its redelivery
  is refused as a duplicate of a step that never committed: the payment is lost (item 6). Its claims also expire after
  a window, 24 hours by default, not with the saga. Rejected.
- **The event routed as itself** to the saga's queue, with the saga handling the contract type: the Kit's pre-built
  handler shadows the saga's chain (item 1). Rejected.
- **The claims kept in the saga's own state** (considered, not tried). The check would have to run after Wolverine loads
  the saga and before it calls the step, and Wolverine's chain has no frame there but `ResequencerSaga`'s ordering
  guard, whose `ShouldProceed` is not virtual (`src/Wolverine/Saga.cs:83`; `SagaChain.cs:285-287` places middleware
  before the load), read; it would also die with the saga, which item 3 shows is too early.

## Findings

- **A saga's steps take the Kit's input type, not the contract.** `Start(SagaInput<FulfilmentSaga, OrderPlacedV1>
  placed)` reads the event as `placed.Event`, and Wolverine reads the saga's identity from `SagaId`, marked
  `[SagaIdentity]` (item 1). A type per saga and event also keeps two sagas of one event two chains, which Wolverine
  would otherwise refuse with `InvalidSagaException` in the Kit's `Classic` mode (`SagaChain.cs:88-122`, read).
- **The Kit's route returns the event's other routes itself.** A custom route source is consulted first, and once one
  has found routes the first source that is not additive ends the search, whether or not it found any; Wolverine's
  `AgentMessages` comes before `ExplicitRouting` (`src/Wolverine/Runtime/WolverineRuntime.Routing.cs:58-120,280-291`,
  `src/Wolverine/WolverineOptions.MessageTransformations.cs:10-30`). An additive Kit source dropped Billing's route, seen
  in a diagnostic run before the fix, so `SagaRouteSource` returns the endpoints whose subscriptions match the event, as
  `ExplicitRouting` finds them, with `MessageRoute.For`, and ends the search. It reads `Endpoint.Subscriptions`, but not
  the internal `IsFromConvention` that `ExplicitRouting` also reads; the Kit's routes are all explicit. #142 may instead
  have the Outbox write the input type beside the event, which changes `OutboxWriter` (not tried).
- **An event from another Host does not reach a saga yet.** `SubscriptionDelivery` fans a broker envelope out to the
  subscriptions' local queues by name, never through routing, so the Kit's route never sees it (inferred from
  `src/PouryaKit.Messaging.Wolverine/Subscriptions/SubscriptionDelivery.cs`, not tested: the Spike has no broker).
- **The Kit owns the step's transaction through Wolverine's own storage frame.** Wolverine's lightweight storage opens a
  connection and a transaction of its own and keeps both private (`src/Persistence/Wolverine.RDBMS/MessageDatabase.cs:576-595`,
  `Sagas/DatabaseSagaStorage.cs`), so no middleware can write in it. Wolverine's `EnrollAndFetchSagaStorageFrame` takes a
  `SagaSchemaCodegen` whose `HelperType` is the class generated code calls (`src/Wolverine/Persistence/Sagas/EnrollAndFetchSagaStorageFrame.cs:95-122`);
  the Kit passes its own, which opens the connection and transaction from the store's data source, claims the step on
  them, enlists the step's context in a `DatabaseEnvelopeTransaction` over them and returns Wolverine's
  `DatabaseSagaStorage` on them. `SagaPersistenceChainPolicy` adds Wolverine's own frame to a chain whose step returns a
  saga before handler policies run (`src/Wolverine/Configuration/SagaPersistenceChainPolicy.cs:5-55`), so the Kit's
  policy replaces it, and `LightweightSagaPersistenceFrameProvider` adds none to a chain that has one
  (`LightweightSagaPersistenceFrameProvider.cs:31-62`). The marker interface it checks is internal, so the frame must be
  Wolverine's class. The schema type, `Wolverine.Postgresql.Internals.DatabaseSagaSchema<,>`, is public but outside
  Wolverine's supported surface by its own comment. All of it is generated into the Release publish (item 7).
- **A step reaches no Kit scoped service while the Host keeps Wolverine's default service location policy**,
  `ServiceLocationPolicy.NotAllowed` (item 8): a saga takes its input, `IMessageContext`, and what Wolverine builds
  without service location. `OperationContextTests.Under_wolverines_default_service_location_policy_a_step_cannot_take_the_kits_operation`
  fails if that default changes; a Host that allows service location gives a step the Kit's `Operation` and its
  publisher (item 8's other test), so #142 keeps the policy for a Host with a saga (an invariant below).
- **The claims outlive the saga, as Receipts do.** Deleting them with the saga's state lets a late copy of the starting
  event start the saga again (item 3's red run), the path ADR 0006's #90 amendment records for Receipts. The Spike keeps
  them; a bounded retention is its own decision, with the same accounting of every redelivery path first.
- **The package's references.** The Kit's storage names `Wolverine.RDBMS` and `Wolverine.Postgresql` types and Npgsql,
  which section C keeps out of `PouryaKit.Messaging.Wolverine` and gives to `PouryaKit.Messaging.Wolverine.EFCore`;
  #142's `Messaging.Wolverine.Sagas` row decides where the package sits.
- **Two steps of one saga at once.** Lightweight storage updates a saga only at the version it loaded, else
  `SagaConcurrencyException` (`DatabaseSagaSchema.cs:107-132`, read); the losing step's claim rolls back with it, and the
  Kit's stored retry runs it again on the new state (inferred). The two payment contracts of one Domain Event reach one
  saga at about the same time and both apply (item 3), whether or not their steps overlapped in that run.
- **Trace context.** The Spike's adapter writes no `tracestate` (ADR 0009); Wolverine writes the step's current activity
  as each envelope's parent id (`MessageBus.cs:428`, read). Not checked by the Spike; #142's.
- **A timeout's identity member.** Wolverine reads a saga's identity from a member of the message named by
  `[SagaIdentity]`, `{Saga}Id`, `{Saga without "Saga"}Id`, `SagaId` or `Id`, before the envelope's saga id
  (`SagaChain.cs:208-226`, `PullSagaIdFromMessageFrame.cs:55-60`), read: a timeout with such a member would leave the
  identity it was scheduled under. `FulfilmentTimeout` has none.

## The registration and step code that worked

A Module's Composition Root, from `spikes/saga/SagaSpike.Shipping/ShippingModule.cs`:

```csharp
module.Publish<OrderFulfilledV1>("shipping.order-fulfilled.v1");
module.Publish<OrderCancelledV1>("shipping.order-cancelled.v1");
module.AddSpikeSaga<FulfilmentSaga>("shipping.fulfilment", systemActor: "shipping.fulfilment", saga => saga
    .Input<OrderPlacedV1>(placed => placed.OrderId)        // the business id; the Kit adds the tenant
    .Input<PaymentReceivedV1>(paid => paid.OrderId)
    .Input<PaymentReceivedV2>(paid => paid.OrderId)
    .Timeout<FulfilmentTimeout>());
```

The saga, abridged from `FulfilmentSaga.cs`:

```csharp
public sealed class FulfilmentSaga : PouryaKitSaga                // public: Wolverine generates its chain
{
    public static (FulfilmentSaga, FulfilmentTimeout) Start(SagaInput<FulfilmentSaga, OrderPlacedV1> placed) =>
        (new FulfilmentSaga { Order = placed.Event.OrderId, Installments = placed.Event.Installments },
         new FulfilmentTimeout(TimeSpan.FromSeconds(placed.Event.PayWithinSeconds)));   // the Kit sets Id, Tenant, Correlation

    public async Task Handle(SagaInput<FulfilmentSaga, PaymentReceivedV1> paid, IMessageContext context)
    {
        if (++Paid < Installments) return;
        await PublishAsync(context, new OrderFulfilledV1(Order, Paid));   // the Kit's adapter
        MarkCompleted();
    }
}

public sealed record FulfilmentTimeout(TimeSpan Within) : TimeoutMessage(Within);    // no identity member
```

The Kit's chain, applied by an `IWolverineExtension` after every Composition Root, abridged from `SagaWolverineExtension.cs`
and `SagaSteps.cs`:

```csharp
wolverine.Discovery.IncludeType(saga.SagaType);                                     // discovery is off in a Kit Host
foreach (var input in saga.Inputs) wolverine.PublishMessage(input.InputType).ToLocalQueue(saga.Name);
foreach (var timeout in saga.Timeouts) wolverine.PublishMessage(timeout).ToLocalQueue(saga.Name);
wolverine.PublishWithMessageRoutingSource(new SagaRouteSource(sagas, options));     // the event's routes, and each saga's
wolverine.Policies.Add(new SagaStepPolicy(sagas));

// SagaStepPolicy, for each declared saga's chain:
chain.Middleware.RemoveAll(frame => frame is EnrollAndFetchSagaStorageFrame);
chain.Middleware.Insert(0, new EnrollAndFetchSagaStorageFrame(typeof(string), chain.SagaType,
    new SagaSchemaCodegen(typeof(DatabaseSagaSchema<,>).MakeGenericType(chain.SagaType, typeof(string)), typeof(SagaStorage))));
chain.Middleware.Insert(1, new SkipClaimedStepFrame(chain.SagaType));   // if (SagaStorage.IsClaimed(storage)) return;
```

What the generated chain then calls, abridged from `SagaStorage.EnrollAndFetchSagaStorage`:

```csharp
if (envelope.Message is ISagaInput { Tenant: null }) throw ...;          // Tenant.Required, before any step
var connection = await database.DataSource.OpenConnectionAsync();
var transaction = await connection.BeginTransactionAsync();
var input = envelope.Message is ISagaInput ? InputOf(envelope, null)    // a timeout's identity: read from the saga's
    : await schema.LoadAsync(sagaId, transaction, ...) is PouryaKitSaga scheduler ? InputOf(envelope, scheduler) : null; // state
var claimed = input is not { } step || await ClaimAsync(connection, transaction, ..., step, ...); // insert ... on conflict
                                                                          // do nothing: waits for a concurrent claim to end
await context.EnlistInOutboxAsync(new DatabaseEnvelopeTransaction(database, transaction));  // emissions and timeouts
return new SagaStepStorage<TId, TSaga>(new DatabaseSagaStorage<TId, TSaga>(connection, transaction, schema), ..., !claimed);
```

The claim is a row of `<store schema>.pouryakit_saga_steps (saga, input, event_id, saga_id, claimed_at)`, primary key
`(saga, input, event_id)`: the saga's name, the input's wire name and the event's identity, for a timeout its type's
name and the identity its saga's state kept for it. A saga's identity is `Uri.EscapeDataString(tenant)` and
`Uri.EscapeDataString(business id)` joined by `:`. A step's identity is a name-based UUID (RFC 9562 version 8) of the
saga's name, the saga's identity, the input's wire name and the input's identity; an emission's identity one of the
step's identity and the emission's wire name, as `SubscriptionDelivery` derives a Snapshot's; and a timeout's identity
one of the scheduling step's identity, the timeout's wire name and its ordinal, counted from 0 for each type in the
order the step scheduled its timeouts, kept in `PouryaKitSaga.Timeouts` by the timeout's envelope id when the Kit stores
the saga, from the envelopes the step stored (`MessageContext.Outstanding`).

## Pinned versions and licences

Read from the restored nuspecs on 2026-10-18. The Spike adds no package to the Kit's closure: every package it uses is
already in `PouryaKit.Messaging.Wolverine.EFCore`'s, at the same version.

| Package | Version | Licence |
|---|---|---|
| WolverineFx, WolverineFx.RDBMS, WolverineFx.Postgresql | 6.48.3, the Kit's line | MIT |
| WolverineFx.RuntimeCompilation | 6.48.3, the Host's Debug build only | MIT |
| JasperFx | 2.80.2 | MIT |
| Weasel.Postgresql | 9.41.0 | MIT |

The Spike's own: Microsoft.Extensions.Hosting 10.0.12, xunit.v3 4.0.1 and Testcontainers.PostgreSql 4.15.0, the Kit's
pins.

## Invariants for permanent tests

All of them are #142's, on the delivery and Gateway seam of `ModularMonolith.Tests` unless the row says otherwise.

| Invariant | Seam | Owner |
|---|---|---|
| A saga and a Kit subscription on one Integration Event each receive it once, through the Kit's route to the saga's own durable queue; two sagas of one event are two chains | Delivery | #142 |
| The event's other routes survive the Kit's route: every subscription of the event keeps its delivery | Delivery | #142 |
| A saga's state and its claims are rows of the message store's schema, a non-default one included; a step's state, claim and stored envelopes commit together | Delivery | #142 |
| One event delivered under two envelope ids is one step; two contracts with one identity are two; a copy of the starting event after the saga completed starts nothing | Delivery | #142 |
| A saga's identity is the event's tenant with its business id, each part escaped: one id in two tenants is two sagas, two tenants whose tenant and business id join alike are two, another tenant's event changes no saga and emits nothing, and an event with no tenant is refused before any step | Delivery | #142 |
| A step's emission, a timeout's included, reaches a tenant-scoped subscription under the declared system Actor, the starting event's tenant and correlation, the step's identity as causation, and an identity Billing's Receipt keys and every delivery of the step repeats; a timeout's step takes the stable identity the saga's state kept when it was scheduled, and two timeouts of one type from one step keep two identities and both apply | Delivery | #142 |
| A step committed before its process dies is not applied again after restart; a step that dies before its commit is applied once | Delivery, child process | #142 |
| A Host with a saga carries its generated chains, the Kit's storage and claim included, in its Release publish; without `codegen write` it does not start outside Development | `validate.py`, as for the Proving Sample's Hosts | #142 |
| A step has no Operation Context: the Kit's publisher refuses it | Delivery | #142 |
| A Host with a saga keeps Wolverine's `ServiceLocationPolicy.NotAllowed`, so a step reaches no Kit scoped service, or the Host fails when it is built: the policy is a value of the built container's `WolverineOptions`, which the Kit's extensions are applied to when they are resolved, before the Host starts (`HostTests.The_host_joins_a_configured_broker_under_its_derived_service_name`, read), so an `ICompositionCheck` answers it, as for every guarantee the final registrations show. The Spike checks it nowhere | Composition check | #142 |
| An event that names no saga is refused with an error the Kit names, as `UnknownSubscriptionException` replaced a bare lookup failure for a subscription; the Spike shows Wolverine's `UnknownSagaException` | Delivery | #142 |
| A step's emission carries the trace context ADR 0009 gives every envelope the Outbox writes; the Spike's adapter writes no `tracestate` | Delivery | #142 |
| An Integration Event from another Host reaches a saga (the broker fan-out delivers its input type to each saga's queue); not built by the Spike | Delivery, two Hosts | #142 |
| The claims' table is created with the message store, not at a saga's first step as the Spike does | Host | #142 |
| One step emits at most one Integration Event of each type, as one Domain Event yields at most one: an emission's identity has no ordinal, unlike a timeout's; not built by the Spike | Delivery | #142 |
| A declared saga is public, each input has a wire name, and a timeout type has no identity member, or the Host fails when it is built | Composition check | #142 |

## Consequences

- ADR 0001 and ADR 0006 carry dated amendments of 2026-10-18 pointing here. The package follows in #142, which turns the
  invariants into tests over merged code; nothing of `spikes/saga/` is merged.
- A Saga is a fourth kind of reaction beside G.2's three, and the only one whose handler is a public type Wolverine
  generates code for. Its step is no Operation: no authorizer, validator, Unit of Work or Receipt runs for it, and the
  claim in the step's transaction does the Receipt's work.
- A saga's state is a third write model, beside ADR 0004's rows committed through the EF outbox and ADR 0011's Streams
  committed through Wolverine's Marten outbox. ADR 0011's rule extends to it: a step has one commit owner, its own
  transaction on the message store's database, which the Kit opens and Wolverine's chain commits, and no Operation
  commits through it. Its emissions and timeouts go into its Module's Outbox, the message store, in that transaction,
  written by the Kit's saga storage through a `DatabaseEnvelopeTransaction` rather than by a Unit of Work. The saga's
  code changes its state and asks the Kit's adapter to emit; it never saves, commits or writes the Outbox itself
  (ADR 0003).
- The exception rests on Wolverine members a later version may move: `SagaSchemaCodegen.HelperType`, the public
  `EnrollAndFetchSagaStorageFrame` and `DatabaseSagaSchema<,>`, the route sources' precedence, and `MessageRoute.For`.
  #142's invariants are what catch a change.
- Plan I.3's Saga row and I.4's Saga paragraph carry dated notes pointing here; the plan keeps its record.

## Amendment (2026-10-18, ticket #142: the package as built)

The decision above stands. `PouryaKit.Messaging.Wolverine.Sagas` builds it on the Kit's seams, and each invariant above is
a permanent test of `ModularMonolith.Tests.SagaTests` or of the structure tests, run once with its behaviour switched off
(the [acceptance map](../acceptance.md#saga-package-ticket-142) names each). Where the package departs from the Spike's
shape:

- **A saga's Module names no Wolverine type but the base of its timeouts.** A step emits with `PublishAsync(event)`, which
  takes no `IMessageContext`: the Kit's guard frame, a synchronous call in the generated chain after the storage frame,
  makes the step current, so a static `Start` reaches it too, and `MarkCompleted` is the Kit's. A timeout derives from
  the Kit's `SagaTimeout`; the members the compiler writes for a record call its base record's own, so the Module still
  names Wolverine's `TimeoutMessage`, which the transport rule allows only when every timeout of the assembly is the
  Kit's. The public-types rule admits a `PouryaKitSaga` and a `SagaTimeout`; a saga or a timeout of Wolverine's own fails
  both rules.
- **The claims table is created with the message store.** Wolverine keeps a PostgreSQL store's list of tables internal,
  so the Kit creates `pouryakit_saga_steps` in the store's schema as the Host starts, before Wolverine does, under the
  advisory lock Wolverine migrates the store under, which serialises instances that start at once; a saga's state table
  is migrated with the store from the `SagaTableDefinition` the declaration registers. No step creates either.
- **The Kit names its refusals.** An Integration Event with no tenant, and an advancing one whose identity names a saga
  that does not exist, another tenant's or one that completed, fail with `SagaInputRefusedException` before any step, in
  place of Wolverine's `UnknownSagaException`. A timeout whose saga completed ends unread, with no claim, as Wolverine's
  chain returns for it: the gap the Spike's second review recorded is now a test.
- **One emission of each type per step**, refused at the second, since an emission's identity derives from its step and
  wire name alone. Emissions are written through `OutboxWriter`'s emission write, which writes the headers `WriteAsync`
  gives an Integration Event and continues the input's trace (ADR 0009): Wolverine's span of the step is the parent when
  Wolverine's source is listened, else the Kit writes the input's own parent, and the tracestate is the input's.
- **The service location check runs after the routing check.** Resolving Wolverine's options applies the Kit's
  extensions, which name every routed type, so `AddPouryaKitSaga` registers `RoutingCheck` before its own check, and the
  check reads the options only once the Host's wire names are declared and its declarations are sound.
- **An Integration Event from another Host reaches a saga.** The Messaging package records each input a saga takes beside
  its subscriptions; the Host's broker queue then binds its wire name and fans the event out to the saga's queue as its
  input, as to a subscription's.
- **The crash between commit and mark is a test of its own process.** The package has no switches, so the child process
  is killed while a trigger the test installs refuses the mark of the payment step's envelope, in place of the Spike's
  probe after the commit.
- **The Release publish** is package validation's: a composition of `scripts/packaging/inventory.json` publishes the test
  Modules' Host with codegen write, as the Proving Sample's Hosts are; the Proving Sample declares no saga.
