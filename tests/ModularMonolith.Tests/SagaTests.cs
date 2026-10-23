using System.Diagnostics;
using System.Text.Json;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModularMonolith.Tests.Sagas;
using ModularMonolith.Tests.Sagas.Host;
using PouryaKit.Cqrs;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Messaging.Wolverine.RabbitMq;
using PouryaKit.Messaging.Wolverine.Sagas;
using PouryaKit.Modularity;
using PouryaKit.Observability;
using PouryaKit.Testing;
using Wolverine;
using Wolverine.Runtime.Handlers;
using Wolverine.Util;

namespace ModularMonolith.Tests;

// The Saga package (ticket #142, ADR 0013): the Shipping Module's Sagas beside Orders, which publishes, and Billing, a
// tenant-scoped Module that subscribes, on one message store, against PostgreSQL. Each test composes its Hosts over a
// database of its own; events are published by Orders through the Kit's Outbox, or, where a test needs one event under two
// envelope ids or one order in two tenants, through Wolverine's bus with the headers the Outbox writes, as a broker's
// fan-out after a crash or a republish delivers them. On the broker collection, whose PostgreSQL and RabbitMQ serve the
// Host that receives from another.
[Collection<BrokerCollection>]
public sealed class SagaTests(BrokerFixture broker) : IAsyncLifetime
{
    private const string Fulfilment = ShippingModule.Fulfilment;
    private const string TraceState = "kit=142,other=v1";

    // The EF Modules' tables, as their migrations and the Kit's migration helpers would create them.
    private const string ModuleTables = """
        create schema orders;
        create table orders.orders (id uuid primary key, installments integer not null, pay_within_seconds integer not null, version bigint not null);
        create table orders.pouryakit_receipts (subscription text not null, message_id uuid not null, primary key (subscription, message_id));
        create schema billing;
        create table billing.pouryakit_receipts (subscription text not null, message_id uuid not null, primary key (subscription, message_id));
        """;

    private readonly List<IHost> _hosts = [];
    private string _database = "";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _database = await DatabaseAsync();

    [Fact]
    public async Task A_saga_and_a_kit_subscription_on_one_integration_event_each_receive_it_once_and_two_sagas_of_one_event_are_two()
    {
        var host = await StartAsync();
        var order = Guid.CreateVersion7();

        await PlaceAsync(host, "tenant-a", order, installments: 3);
        await PublishAsync(host, new ReferenceOpenedV1("r-1"), Guid.CreateVersion7(), "tenant-a");

        var reference = SagaStorage.SagaId("tenant-a", "r-1");
        await WaitUntilAsync($"exists (select from wolverine.shipping_fulfilment_saga where id = '{FulfilmentOf("tenant-a", order)}')");
        await WaitUntilAsync(
            $"exists (select from wolverine.shipping_reference_saga where id = '{reference}') and exists (select from wolverine.shipping_reminder_saga where id = '{reference}')"
        );
        await SettledAsync();
        Assert.Equal(3, (await SagaAsync("shipping_fulfilment", FulfilmentOf("tenant-a", order)))!.Value.GetProperty("Installments").GetInt32());
        Assert.Equal(1L, await CountAsync("select count(*) from billing.pouryakit_receipts where subscription = 'billing.order-placed'"));
        Assert.Equal("billing.order-placed", Assert.Single(BillingLog.Of(order)).Subscription);
        Assert.Equal(0L, await CountAsync("select count(*) from wolverine.wolverine_dead_letters"));
    }

    // Created with the message store as the Host starts, in its schema, before any step; then each step's state, claim and
    // emission commit in one transaction. The emission is read while Billing holds its delivery, before Wolverine marks it.
    [Fact]
    public async Task A_sagas_state_and_claims_are_rows_of_the_message_stores_schema_created_with_it_and_commit_with_the_steps_emission()
    {
        var host = await StartAsync(schema: "kitstore");
        var order = Guid.CreateVersion7();
        var sagaId = FulfilmentOf("tenant-a", order);

        Assert.Equal(["kitstore"], await SchemasOfAsync(SagaStepTable));
        Assert.Equal(["kitstore"], await SchemasOfAsync("shipping_fulfilment_saga"));
        await PlaceAsync(host, "tenant-a", order, installments: 2);
        await WaitUntilAsync($"exists (select from kitstore.shipping_fulfilment_saga where id = '{sagaId}')");
        var first = Guid.CreateVersion7();
        await PublishAsync(host, new PaymentReceivedV1(order), first, "tenant-a");
        await WaitUntilAsync($"(select (body->>'Paid')::int from kitstore.shipping_fulfilment_saga where id = '{sagaId}') = 1");

        Assert.Single(
            await RowsAsync(
                $"""
                select from kitstore.shipping_fulfilment_saga s, kitstore.{SagaStepTable} c
                where s.id = '{sagaId}' and c.event_id = '{first}' and pg_xact_commit_timestamp(s.xmin) = pg_xact_commit_timestamp(c.xmin)
                """
            )
        );
        var release = BillingLog.Hold(order);
        try
        {
            var last = Guid.CreateVersion7();
            await PublishAsync(host, new PaymentReceivedV1(order), last, "tenant-a");
            await WaitUntilAsync("exists (select from kitstore.wolverine_incoming_envelopes where received_at = 'local://billing.order-fulfilled/')");
            Assert.Single(
                await RowsAsync(
                    $"""
                    select from kitstore.{SagaStepTable} c, kitstore.wolverine_incoming_envelopes e
                    where c.event_id = '{last}' and e.received_at = 'local://billing.order-fulfilled/'
                        and pg_xact_commit_timestamp(c.xmin) = pg_xact_commit_timestamp(e.xmin)
                    """
                )
            );
            Assert.Null(await SagaAsync("shipping_fulfilment", sagaId, "kitstore"));
        }
        finally
        {
            release();
        }

        await WaitUntilAsync("exists (select from billing.pouryakit_receipts where subscription = 'billing.order-fulfilled')");
        Assert.Equal(["kitstore"], await SchemasOfAsync(SagaStepTable));
    }

    [Fact]
    public async Task One_payment_delivered_under_two_envelope_ids_advances_the_saga_once()
    {
        var host = await StartAsync();
        var order = Guid.CreateVersion7();
        await StartedAsync(host, order);
        var payment = Guid.CreateVersion7();

        await PublishAsync(host, new PaymentReceivedV1(order), payment, "tenant-a");
        await PublishAsync(host, new PaymentReceivedV1(order), payment, "tenant-a");

        await WaitUntilAsync($"{HandledAt(Fulfilment)} = 3");
        Assert.Equal(1, await PaidAsync("tenant-a", order));
        Assert.Equal(1L, await CountAsync($"select count(*) from wolverine.{SagaStepTable} where event_id = '{payment}'"));
    }

    // Orders' one Notification Handler publishes both payment contracts for one Domain Event, under its one identity.
    [Fact]
    public async Task Two_contracts_published_for_one_payment_both_reach_the_saga()
    {
        var host = await StartAsync();
        var order = Guid.CreateVersion7();
        await StartedAsync(host, order);

        await PayAsync(host, "tenant-a", order);

        var sagaId = FulfilmentOf("tenant-a", order);
        await WaitUntilAsync(
            $"(select (body->>'Paid')::int = 1 and body->'Methods' = '[\"card\"]'::jsonb from wolverine.shipping_fulfilment_saga where id = '{sagaId}')"
        );
        Assert.Equal(await ClaimedAsync(sagaId, "orders.payment-received.v1"), await ClaimedAsync(sagaId, "orders.payment-received.v2"));
    }

    // The claims outlive the saga: a late copy of the starting event finds its claim and starts nothing.
    [Fact]
    public async Task A_copy_of_the_starting_event_delivered_after_the_saga_completed_starts_no_saga()
    {
        var host = await StartAsync();
        var order = Guid.CreateVersion7();
        var sagaId = FulfilmentOf("tenant-a", order);
        await StartedAsync(host, order, installments: 1);
        var placed = await ClaimedAsync(sagaId, "orders.order-placed.v1");
        await PayAsync(host, "tenant-a", order);
        await WaitUntilAsync("exists (select from billing.pouryakit_receipts where subscription = 'billing.order-fulfilled')");
        Assert.Null(await SagaAsync("shipping_fulfilment", sagaId));

        await PublishAsync(host, new OrderPlacedV1(order, 1, 3600), placed, "tenant-a");

        await WaitUntilAsync($"{HandledAt(Fulfilment, "message_type = 'shipping.fulfilment.orders.order-placed.v1'")} = 2");
        Assert.Null(await SagaAsync("shipping_fulfilment", sagaId));
        Assert.Equal(1L, await CountAsync("select count(*) from billing.pouryakit_receipts where subscription = 'billing.order-fulfilled'"));
    }

    [Fact]
    public async Task One_order_id_in_two_tenants_is_two_sagas_and_a_payment_advances_only_its_own_tenants()
    {
        var host = await StartAsync();
        var order = Guid.CreateVersion7();

        await PublishAsync(host, new OrderPlacedV1(order, 2, 3600), Guid.CreateVersion7(), "tenant-a");
        await PublishAsync(host, new OrderPlacedV1(order, 3, 3600), Guid.CreateVersion7(), "tenant-b");
        await WaitUntilAsync($"(select count(*) from wolverine.shipping_fulfilment_saga where body->>'Order' = '{order}') = 2");
        await PublishAsync(host, new PaymentReceivedV1(order), Guid.CreateVersion7(), "tenant-b");

        await WaitUntilAsync($"(select (body->>'Paid')::int from wolverine.shipping_fulfilment_saga where id = '{FulfilmentOf("tenant-b", order)}') = 1");
        await SettledAsync();
        var a = (await SagaAsync("shipping_fulfilment", FulfilmentOf("tenant-a", order)))!.Value;
        Assert.Equal(("tenant-a", 2, 0), (a.GetProperty("Tenant").GetString(), a.GetProperty("Installments").GetInt32(), a.GetProperty("Paid").GetInt32()));
        Assert.Equal("tenant-b", (await SagaAsync("shipping_fulfilment", FulfilmentOf("tenant-b", order)))!.Value.GetProperty("Tenant").GetString());
        Assert.Equal(0L, await CountAsync("select count(*) from wolverine.wolverine_dead_letters"));
    }

    // Each part of the identity is escaped, so tenant "a:b" with the reference "c" and tenant "a" with "b:c" are two.
    [Fact]
    public async Task Two_tenants_whose_tenant_and_business_id_join_alike_are_two_sagas()
    {
        var host = await StartAsync();

        await PublishAsync(host, new ReferenceOpenedV1("c"), Guid.CreateVersion7(), "a:b");
        await PublishAsync(host, new ReferenceOpenedV1("b:c"), Guid.CreateVersion7(), "a");

        await WaitUntilAsync("(select count(*) from wolverine.shipping_reference_saga) = 2");
        Assert.Equal(
            [["a", "b:c"], ["a:b", "c"]],
            await RowsAsync("select body->>'Tenant', body->>'Reference' from wolverine.shipping_reference_saga order by 1")
        );
        Assert.Equal(0L, await CountAsync("select count(*) from wolverine.wolverine_dead_letters where received_at = 'local://shipping.reference/'"));
    }

    // Its identity names its own tenant, where no saga exists: the Kit refuses it before any step, and after its retries
    // the delivery is set aside.
    [Fact]
    public async Task A_payment_of_another_tenant_is_refused_by_the_kit_before_any_step_and_changes_and_emits_nothing()
    {
        var host = await StartAsync();
        var order = Guid.CreateVersion7();
        await PublishAsync(host, new OrderPlacedV1(order, 1, 3600), Guid.CreateVersion7(), "tenant-a");
        await WaitUntilAsync($"exists (select from wolverine.shipping_fulfilment_saga where id = '{FulfilmentOf("tenant-a", order)}')");
        var payment = Guid.CreateVersion7();

        await PublishAsync(host, new PaymentReceivedV1(order), payment, "tenant-c");

        await WaitUntilAsync($"exists (select from wolverine.wolverine_dead_letters where received_at = 'local://{Fulfilment}/')");
        Assert.Equal(
            [[typeof(SagaInputRefusedException).FullName!]],
            await RowsAsync($"select exception_type from wolverine.wolverine_dead_letters where received_at = 'local://{Fulfilment}/'")
        );
        Assert.Equal(0, await PaidAsync("tenant-a", order));
        Assert.Equal(0L, await CountAsync($"select count(*) from wolverine.{SagaStepTable} where event_id = '{payment}'"));
        Assert.Equal(0L, await CountAsync("select count(*) from wolverine.wolverine_incoming_envelopes where received_at = 'local://billing.order-fulfilled/'"));
    }

    [Fact]
    public async Task An_event_with_no_tenant_is_refused_by_the_kit_before_any_step()
    {
        var host = await StartAsync();
        var order = Guid.CreateVersion7();

        await PublishAsync(host, new OrderPlacedV1(order, 1, 3600), Guid.CreateVersion7(), tenant: null);

        await WaitUntilAsync($"exists (select from wolverine.wolverine_dead_letters where received_at = 'local://{Fulfilment}/')");
        var refused = Assert.Single(await RowsAsync($"select exception_type, exception_message from wolverine.wolverine_dead_letters where received_at = 'local://{Fulfilment}/'"));
        Assert.Equal(typeof(SagaInputRefusedException).FullName, refused[0]);
        Assert.Contains("with no tenant", (string)refused[1], StringComparison.Ordinal);
        Assert.Equal(0L, await CountAsync("select count(*) from wolverine.shipping_fulfilment_saga"));
        Assert.Equal(0L, await CountAsync($"select count(*) from wolverine.{SagaStepTable}"));
    }

    [Fact]
    public async Task A_completing_steps_emission_reaches_a_tenant_scoped_subscription_under_the_sagas_actor_and_the_starting_events_tenant_and_correlation()
    {
        var host = await StartAsync();
        var order = Guid.CreateVersion7();
        var sagaId = FulfilmentOf("tenant-a", order);
        var (placing, paying) = (Context("tenant-a"), Context("tenant-a"));
        await PlaceAsync(host, placing, order, installments: 1);
        await WaitUntilAsync($"exists (select from wolverine.shipping_fulfilment_saga where id = '{sagaId}')");

        await PayAsync(host, paying, order);

        await WaitUntilAsync("exists (select from billing.pouryakit_receipts where subscription = 'billing.order-fulfilled')");
        var step = SagaStorage.StepId(Fulfilment, sagaId, "orders.payment-received.v1", await ClaimedAsync(sagaId, "orders.payment-received.v1"));
        var fulfilled = Assert.Single(BillingLog.Of(order), delivery => delivery.Subscription == "billing.order-fulfilled").Context;
        Assert.Equal(
            (ActorKind.System, Fulfilment, "tenant-a", placing.CorrelationId, step),
            (fulfilled.Actor.Kind, fulfilled.Actor.Id, fulfilled.Tenant, fulfilled.CorrelationId, fulfilled.CausationId)
        );
        Assert.NotEqual(paying.CorrelationId, fulfilled.CorrelationId);
        Assert.Equal(
            [[SagaStorage.EmissionId(step, "shipping.order-fulfilled.v1")]],
            await RowsAsync("select message_id from billing.pouryakit_receipts where subscription = 'billing.order-fulfilled'")
        );
    }

    // A timeout carries no Operation Context: its step's emission takes the tenant and correlation the saga's state kept from
    // the starting event, and the identity the state kept for the timeout when the starting step scheduled it.
    [Fact]
    public async Task A_timeouts_emission_carries_the_identity_tenant_and_correlation_the_sagas_state_kept()
    {
        var host = await StartAsync();
        var order = Guid.CreateVersion7();
        var sagaId = FulfilmentOf("tenant-a", order);
        var placing = Context("tenant-a");
        var timeoutType = typeof(FulfilmentTimeout).ToMessageTypeName();

        await PlaceAsync(host, placing, order, installments: 2, payWithinSeconds: 3);

        await WaitUntilAsync($"exists (select from wolverine.shipping_fulfilment_saga where id = '{sagaId}')");
        var scheduling = SagaStorage.StepId(Fulfilment, sagaId, "orders.order-placed.v1", await ClaimedAsync(sagaId, "orders.order-placed.v1"));
        var timeout = SagaStorage.TimeoutId(scheduling, timeoutType, 0);
        var envelope = (Guid)Assert.Single(await RowsAsync($"select id from wolverine.wolverine_incoming_envelopes where message_type = '{timeoutType}'"))[0];
        var kept = (await SagaAsync("shipping_fulfilment", sagaId))!.Value.GetProperty("Timeouts");
        Assert.Equal([(envelope.ToString(), timeout.ToString())], kept.EnumerateObject().Select(entry => (entry.Name, entry.Value.GetString())));
        Assert.NotEqual(envelope, timeout);

        await WaitUntilAsync("exists (select from billing.pouryakit_receipts where subscription = 'billing.order-cancelled')");
        Assert.Null(await SagaAsync("shipping_fulfilment", sagaId));
        Assert.Equal(timeout, await ClaimedAsync(sagaId, timeoutType));
        var step = SagaStorage.StepId(Fulfilment, sagaId, timeoutType, timeout);
        var cancelled = Assert.Single(BillingLog.Of(order), delivery => delivery.Subscription == "billing.order-cancelled").Context;
        Assert.Equal(
            (ActorKind.System, Fulfilment, "tenant-a", placing.CorrelationId, step),
            (cancelled.Actor.Kind, cancelled.Actor.Id, cancelled.Tenant, cancelled.CorrelationId, cancelled.CausationId)
        );
        Assert.Equal(
            [[SagaStorage.EmissionId(step, "shipping.order-cancelled.v1")]],
            await RowsAsync("select message_id from billing.pouryakit_receipts where subscription = 'billing.order-cancelled'")
        );
    }

    [Fact]
    public async Task Two_timeouts_of_one_type_from_one_step_each_keep_their_own_identity_and_both_apply()
    {
        var host = await StartAsync();
        var sagaId = SagaStorage.SagaId("tenant-a", "r-1");
        var timeoutType = typeof(ReminderTimeout).ToMessageTypeName();

        await PublishAsync(host, new ReferenceOpenedV1("r-1"), Guid.CreateVersion7(), "tenant-a");

        await WaitUntilAsync($"exists (select from wolverine.shipping_reminder_saga where id = '{sagaId}' and jsonb_array_length(body->'Reminded') = 2)");
        var state = (await SagaAsync("shipping_reminder", sagaId))!.Value;
        Assert.Equal([1, 2], state.GetProperty("Reminded").EnumerateArray().Select(delay => delay.GetInt32()).Order());
        var scheduling = SagaStorage.StepId("shipping.reminder", sagaId, "orders.reference-opened.v1", await ClaimedAsync(sagaId, "orders.reference-opened.v1", "shipping.reminder"));
        Guid[] identities = [.. new[] { SagaStorage.TimeoutId(scheduling, timeoutType, 0), SagaStorage.TimeoutId(scheduling, timeoutType, 1) }.Order()];
        Assert.Equal(identities, state.GetProperty("Timeouts").EnumerateObject().Select(entry => entry.Value.GetGuid()).Order());
        Assert.Equal(
            identities,
            (await RowsAsync($"select event_id from wolverine.{SagaStepTable} where saga_id = '{sagaId}' and input = '{timeoutType}'")).Select(row => (Guid)row[0]).Order()
        );
        Assert.Equal(0L, await CountAsync("select count(*) from wolverine.wolverine_dead_letters"));
    }

    // An emission's identity derives from its step and wire name alone, so a second of one type would share it.
    [Fact]
    public async Task A_step_that_emits_one_type_twice_is_refused_and_commits_nothing()
    {
        var host = await StartAsync();
        var order = Guid.CreateVersion7();
        await StartedAsync(host, order);
        var payment = Guid.CreateVersion7();

        await PublishAsync(host, new PaymentReceivedV2(order, "twice"), payment, "tenant-a");

        await WaitUntilAsync($"exists (select from wolverine.wolverine_dead_letters where received_at = 'local://{Fulfilment}/')");
        Assert.Contains(
            "emitted OrderFulfilledV1 twice from one step",
            (string)Assert.Single(await RowsAsync($"select exception_message from wolverine.wolverine_dead_letters where received_at = 'local://{Fulfilment}/'"))[0],
            StringComparison.Ordinal
        );
        Assert.Equal(0, (await SagaAsync("shipping_fulfilment", FulfilmentOf("tenant-a", order)))!.Value.GetProperty("Methods").GetArrayLength());
        Assert.Equal(0L, await CountAsync($"select count(*) from wolverine.{SagaStepTable} where event_id = '{payment}'"));
        Assert.Equal(0L, await CountAsync("select count(*) from wolverine.wolverine_incoming_envelopes where received_at = 'local://billing.order-fulfilled/'"));
    }

    // The emission continues the trace of the step's input (ADR 0009): its parent is Wolverine's span of the step, or the
    // input's own parent when Wolverine's source is not listened, and its tracestate the input's.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_steps_emission_carries_the_trace_context_of_its_input(bool wolverineListened)
    {
        using var telemetry = new RecordedTelemetry(wolverineListened);
        var host = await StartAsync(configure: builder => builder.Services.AddPouryaKitObservability());
        var order = Guid.CreateVersion7();
        await StartedAsync(host, order, installments: 1);

        ActivityTraceId trace;
        using (var upstream = RecordedTelemetry.Upstream.StartActivity("upstream", ActivityKind.Server)!)
        {
            upstream.TraceStateString = TraceState;
            trace = upstream.TraceId;
            await PayAsync(host, "tenant-a", order);
        }

        await WaitUntilAsync("exists (select from billing.pouryakit_receipts where subscription = 'billing.order-fulfilled')");
        var fulfilled = Assert.Single(BillingLog.Of(order), delivery => delivery.Subscription == "billing.order-fulfilled");
        Assert.Equal((trace.ToString(), TraceState), (fulfilled.TraceId, fulfilled.TraceState));
    }

    // The recorded gap of the Spike's second review: a saga completed before its timeout falls due. One payment contract
    // only, since the second would arrive after the saga completed and be refused.
    [Fact]
    public async Task A_timeout_whose_saga_completed_ends_with_no_claim_and_emits_nothing()
    {
        var host = await StartAsync();
        var order = Guid.CreateVersion7();
        var timeoutType = typeof(FulfilmentTimeout).ToMessageTypeName();
        await StartedAsync(host, order, installments: 1, payWithinSeconds: 2);

        await PublishAsync(host, new PaymentReceivedV1(order), Guid.CreateVersion7(), "tenant-a");

        await WaitUntilAsync("exists (select from billing.pouryakit_receipts where subscription = 'billing.order-fulfilled')");
        await WaitUntilAsync($"exists (select from wolverine.wolverine_incoming_envelopes where message_type = '{timeoutType}' and status = 'Handled')", seconds: 30);
        Assert.Equal(0L, await CountAsync($"select count(*) from wolverine.{SagaStepTable} where input = '{timeoutType}'"));
        Assert.Equal(0L, await CountAsync("select count(*) from wolverine.wolverine_dead_letters"));
        Assert.DoesNotContain(BillingLog.Of(order), delivery => delivery.Subscription == "billing.order-cancelled");
    }

    // The final payment and the timeout load one state, and the timeout completes the saga first: the payment's delete
    // finds no state at the version it loaded, so it rolls back with Wolverine's concurrency exception, and its retries
    // find no saga. One terminal emission commits, never two that contradict each other.
    [Fact]
    public async Task Of_two_steps_that_complete_one_saga_from_one_state_only_the_first_to_commit_emits()
    {
        var logs = new RecordedLogs();
        var host = await StartAsync(configure: builder => builder.Logging.AddProvider(logs));
        var order = Guid.CreateVersion7();
        var sagaId = FulfilmentOf("tenant-a", order);
        await StartedAsync(host, order, installments: 1, payWithinSeconds: 3);
        var held = HoldFirstPaymentStep($"not exists (select from wolverine.shipping_fulfilment_saga where id = '{sagaId}')");
        try
        {
            await PublishAsync(host, new PaymentReceivedV1(order), Guid.CreateVersion7(), "tenant-a");
            await held.WaitAsync(TimeSpan.FromSeconds(15), Token);
            await WaitUntilAsync(
                $"exists (select from wolverine.wolverine_dead_letters where received_at = 'local://{Fulfilment}/') or exists (select from billing.pouryakit_receipts where subscription = 'billing.order-fulfilled')",
                seconds: 30
            );
        }
        finally
        {
            SagaProbes.InPaymentStep = null;
        }

        await SettledAsync();
        Assert.Equal(
            [["billing.order-cancelled"]],
            await RowsAsync("select subscription from billing.pouryakit_receipts where subscription in ('billing.order-fulfilled', 'billing.order-cancelled')")
        );
        Assert.Equal(
            [[typeof(SagaInputRefusedException).FullName!]],
            await RowsAsync($"select exception_type from wolverine.wolverine_dead_letters where received_at = 'local://{Fulfilment}/'")
        );
        Assert.Contains(logs.Entries, entry => entry.Exception is SagaConcurrencyException);
    }

    // The second payment contract changes the state a completing payment loaded and commits first: the payment's delete
    // finds another version, so it rolls back with Wolverine's concurrency exception, and its retry completes the saga on
    // the state the other step committed.
    [Fact]
    public async Task A_completing_step_whose_state_another_step_changed_since_it_loaded_it_rolls_back_and_completes_on_the_new_state()
    {
        var logs = new RecordedLogs();
        var host = await StartAsync(configure: builder => builder.Logging.AddProvider(logs));
        var order = Guid.CreateVersion7();
        var sagaId = FulfilmentOf("tenant-a", order);
        await StartedAsync(host, order, installments: 1);
        var held = HoldFirstPaymentStep($"exists (select from wolverine.shipping_fulfilment_saga where id = '{sagaId}' and body->'Methods' = '[\"card\"]'::jsonb)");
        try
        {
            await PublishAsync(host, new PaymentReceivedV1(order), Guid.CreateVersion7(), "tenant-a");
            await held.WaitAsync(TimeSpan.FromSeconds(15), Token);
            await PublishAsync(host, new PaymentReceivedV2(order, "card"), Guid.CreateVersion7(), "tenant-a");
            await WaitUntilAsync("exists (select from billing.pouryakit_receipts where subscription = 'billing.order-fulfilled')", seconds: 30);
        }
        finally
        {
            SagaProbes.InPaymentStep = null;
        }

        await SettledAsync();
        Assert.Null(await SagaAsync("shipping_fulfilment", sagaId));
        Assert.Equal(1L, await CountAsync("select count(*) from billing.pouryakit_receipts where subscription = 'billing.order-fulfilled'"));
        Assert.Equal(0L, await CountAsync("select count(*) from wolverine.wolverine_dead_letters"));
        Assert.Contains(logs.Entries, entry => entry.Exception is SagaConcurrencyException);
    }

    // Killed after the step's commit and before Wolverine marked its envelope handled, which a trigger refuses until the
    // child dies: the redelivery finds the step's claim.
    [Fact]
    public async Task A_step_committed_before_its_process_is_killed_is_not_applied_again_after_restart()
    {
        await (await StartAsync()).StopAsync(Token);
        await RowsAsync(
            """
            create function wolverine.refuse_payment_mark() returns trigger language plpgsql as $$
            begin
                if new.status = 'Handled' and old.message_type = 'shipping.fulfilment.orders.payment-received.v1' then
                    raise exception 'the test refuses the mark of a payment step';
                end if;
                return new;
            end $$;
            create trigger refuse_payment_mark before update on wolverine.wolverine_incoming_envelopes
                for each row execute function wolverine.refuse_payment_mark();
            """
        );
        var order = Guid.CreateVersion7();

        using (var child = await ChildAsync("PAID", order))
        {
            await WaitUntilAsync($"(select (body->>'Paid')::int from wolverine.shipping_fulfilment_saga where id = '{FulfilmentOf("tenant-a", order)}') = 1");
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(Token);
        }

        Assert.Equal(1, await PaidAsync("tenant-a", order));
        Assert.Equal(1L, await CountAsync($"select count(*) from wolverine.wolverine_incoming_envelopes where {PaymentStep} and status = 'Incoming' and owner_id <> 0"));
        await RowsAsync("drop trigger refuse_payment_mark on wolverine.wolverine_incoming_envelopes");

        await StartAsync();

        await WaitUntilAsync($"exists (select from wolverine.wolverine_incoming_envelopes where {PaymentStep} and status = 'Handled')", seconds: 30);
        await SettledAsync();
        Assert.Equal(1, await PaidAsync("tenant-a", order));
        Assert.Equal(1L, await CountAsync($"select count(*) from wolverine.{SagaStepTable} where input = 'orders.payment-received.v1'"));
    }

    // Killed inside the step, before its commit: its claim rolls back with it, so the redelivery applies the step once.
    [Fact]
    public async Task A_step_whose_process_is_killed_before_it_commits_is_applied_once_after_restart()
    {
        var order = Guid.CreateVersion7();

        using (var child = await ChildAsync("IN STEP", order, "in-step"))
        {
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(Token);
        }

        Assert.Equal(0, await PaidAsync("tenant-a", order));
        Assert.Equal(0L, await CountAsync($"select count(*) from wolverine.{SagaStepTable} where input = 'orders.payment-received.v1'"));

        await StartAsync();

        await WaitUntilAsync($"exists (select from wolverine.wolverine_incoming_envelopes where {PaymentStep} and status = 'Handled')", seconds: 30);
        await SettledAsync();
        Assert.Equal(1, await PaidAsync("tenant-a", order));
    }

    // The Host that receives from another: Orders publishes in its own Host to the broker; Shipping's Host receives the
    // event at its queue and fans it out to the saga's queue as the saga's input.
    [Fact]
    public async Task An_integration_event_from_another_host_reaches_a_saga_through_the_broker_fan_out()
    {
        var environment = await broker.CreateEnvironmentAsync();
        await Sql.RowsAsync(environment.Catalog, ModuleTables);
        await StartBrokerHostAsync(
            environment.Inventory,
            environment.Broker,
            "shipping",
            builder =>
            {
                builder.AddPouryaKitModules(modules => modules.Add<ShippingModule>());
                builder.Services.Receive<OrderPlacedV1>("orders.order-placed.v1");
                builder.Services.Receive<PaymentReceivedV1>("orders.payment-received.v1");
                builder.Services.Receive<PaymentReceivedV2>("orders.payment-received.v2");
                builder.Services.Receive<ReferenceOpenedV1>("orders.reference-opened.v1");
            }
        );
        var orders = await StartBrokerHostAsync(environment.Catalog, environment.Broker, "orders", builder => builder.AddPouryaKitModules(modules => modules.Add<OrdersModule>()));
        var order = Guid.CreateVersion7();

        await PlaceAsync(orders, "tenant-a", order, installments: 2);

        await Sql.WaitUntilAsync(
            environment.Inventory,
            $"exists (select from wolverine.shipping_fulfilment_saga where id = '{FulfilmentOf("tenant-a", order)}')",
            "The saga in the receiving Host never started.",
            seconds: 30
        );
        Assert.Equal([["tenant-a", 2]], await Sql.RowsAsync(environment.Inventory, "select body->>'Tenant', (body->>'Installments')::int from wolverine.shipping_fulfilment_saga"));
    }

    // ADR 0013: under any other policy, a step could reach the Kit's scoped services, the Operation and its publisher among
    // them, outside every Operation. Wolverine's options are read once the routing check declared the wire names.
    [Fact]
    public void A_host_with_a_saga_whose_service_location_policy_is_not_wolverines_default_fails_when_it_is_built()
    {
        var refused = Assert.Throws<InvalidOperationException>(() =>
            Compose(configure: builder => builder.Services.ConfigureWolverine(wolverine => wolverine.ServiceLocationPolicy = ServiceLocationPolicy.AllowedButWarn))
        );

        Assert.Contains("Wolverine's ServiceLocationPolicy is AllowedButWarn", refused.Message, StringComparison.Ordinal);
    }

    // A step has no Operation Context: under the policy the Host keeps, a step that takes the Kit's Operation is refused when
    // Wolverine generates its chain, at its first message, and the delivery is set aside.
    [Fact]
    public async Task A_step_that_takes_the_kits_operation_is_refused_when_wolverine_generates_its_chain()
    {
        var host = await StartAsync(configure: WithProbes);

        await PlaceAsync(host, "tenant-a", Guid.CreateVersion7());

        await WaitUntilAsync("exists (select from wolverine.wolverine_dead_letters where received_at = 'local://probes.context/')");
        Assert.Equal(
            [["Wolverine.Configuration.InvalidServiceLocationException"]],
            await RowsAsync("select exception_type from wolverine.wolverine_dead_letters where received_at = 'local://probes.context/'")
        );
    }

    // A Saga takes and emits Integration Events only (ADR 0013): a Snapshot its Module publishes is refused at the step,
    // before anything commits.
    [Fact]
    public async Task A_step_that_emits_a_snapshot_is_refused_and_commits_nothing()
    {
        var host = await StartAsync(configure: WithProbes);

        await PublishAsync(host, new ReferenceOpenedV1("s-1"), Guid.CreateVersion7(), "tenant-a");

        await WaitUntilAsync("exists (select from wolverine.wolverine_dead_letters where received_at = 'local://probes.snapshot/')");
        Assert.Contains(
            $"emitted {typeof(ShelfSnapshotV1).FullName}, a Snapshot",
            (string)Assert.Single(await RowsAsync("select exception_message from wolverine.wolverine_dead_letters where received_at = 'local://probes.snapshot/'"))[0],
            StringComparison.Ordinal
        );
        Assert.Equal(0L, await CountAsync("select count(*) from wolverine.probes_snapshot_saga"));
    }

    // What a step returns is cascaded by Wolverine without the Kit's headers, so the step fails before its commit and
    // nothing it returned is sent: a step emits with PublishAsync.
    [Fact]
    public async Task A_step_that_returns_an_integration_event_is_refused_and_sends_nothing()
    {
        var host = await StartAsync(configure: WithProbes);

        await PublishAsync(host, new ReferenceOpenedV1("c-1"), Guid.CreateVersion7(), "tenant-a");

        await WaitUntilAsync("exists (select from wolverine.wolverine_dead_letters where received_at = 'local://probes.cascade/')");
        var refused = (string)Assert.Single(await RowsAsync("select exception_message from wolverine.wolverine_dead_letters where received_at = 'local://probes.cascade/'"))[0];
        Assert.Contains($"returned {typeof(OrderFulfilledV1).FullName}", refused, StringComparison.Ordinal);
        Assert.Contains("PublishAsync", refused, StringComparison.Ordinal);
        Assert.Equal(0L, await CountAsync("select count(*) from wolverine.wolverine_incoming_envelopes where received_at = 'local://billing.order-fulfilled/'"));
        Assert.Equal(0L, await CountAsync("select count(*) from wolverine.probes_cascade_saga"));
    }

    // Wolverine takes each step name with an Async suffix: one StartOrHandleAsync starts the saga, then advances it.
    [Fact]
    public async Task A_step_named_start_or_handle_async_starts_the_saga_and_then_advances_it()
    {
        var host = await StartAsync();
        var sagaId = SagaStorage.SagaId("tenant-a", "t-1");

        await PublishAsync(host, new ReferenceOpenedV1("t-1"), Guid.CreateVersion7(), "tenant-a");
        await WaitUntilAsync($"exists (select from wolverine.shipping_tally_saga where id = '{sagaId}')");
        await PublishAsync(host, new ReferenceOpenedV1("t-1"), Guid.CreateVersion7(), "tenant-a");

        await WaitUntilAsync($"exists (select from wolverine.shipping_tally_saga where id = '{sagaId}' and (body->>'Count')::int = 2)");
        Assert.Equal(0L, await CountAsync("select count(*) from wolverine.wolverine_dead_letters where received_at = 'local://shipping.tally/'"));
    }

    // Wolverine names a saga's table after its type's simple name and an input after its arguments' simple names: the Kit
    // names both after the saga, so two sagas of one simple name in two namespaces keep their states and inputs apart.
    [Fact]
    public async Task Two_sagas_of_one_simple_name_in_two_namespaces_keep_their_states_and_their_inputs_apart()
    {
        var host = await StartAsync(configure: builder => builder.AddPouryaKitModules(modules => modules.Add<TwinsModule>()));
        var sagaId = SagaStorage.SagaId("tenant-a", "w-1");

        await PublishAsync(host, new ReferenceOpenedV1("w-1"), Guid.CreateVersion7(), "tenant-a");

        await WaitUntilAsync(
            $"{HandledAt("tests.twin")} + {HandledAt("tests.other-twin")} + (select count(*) from wolverine.wolverine_dead_letters where received_at like 'local://tests.%') >= 2"
        );
        Assert.Equal(0L, await CountAsync("select count(*) from wolverine.wolverine_dead_letters where received_at like 'local://tests.%'"));
        Assert.Equal(
            [["local://tests.other-twin/", "tests.other-twin.orders.reference-opened.v1"], ["local://tests.twin/", "tests.twin.orders.reference-opened.v1"]],
            await RowsAsync("select received_at, message_type from wolverine.wolverine_incoming_envelopes where received_at like 'local://tests.%' order by 1")
        );
        Assert.Equal(
            [["this", "other"]],
            await RowsAsync($"select (select body->>'Twin' from wolverine.tests_twin_saga where id = '{sagaId}'), (select body->>'Twin' from wolverine.tests_other_twin_saga where id = '{sagaId}')")
        );
    }

    [Theory]
    [InlineData("hidden", "The Saga tests.hidden, ModularMonolith.Tests.HiddenSaga, is not a public concrete class with a public parameterless constructor")]
    [InlineData("unpublished", "The Saga tests.unpublished takes ModularMonolith.Tests.UnpublishedV1, which no Module of this Host publishes and no Receive declares")]
    [InlineData("identified", "The timeout ModularMonolith.Tests.IdentifiedTimeout of the Saga tests.identified has the member SagaId")]
    [InlineData("queue", "The Saga ModularMonolith.Tests.QueueSaga and a subscription or a delayed command have the name billing.order-placed")]
    [InlineData("unowned", "The Saga tests.unowned, ModularMonolith.Tests.QueueSaga, is declared without a Module in a Host of Modules")]
    [InlineData("table", "would keep their states in one table, tests_twin_saga")]
    [InlineData("digit", "The Saga 2fa.enrolment's state table, 2fa_enrolment_saga, does not start with a letter")]
    [InlineData("input", "would be named orders.orders.order-placed.v1, ignoring case, which they share")]
    [InlineData("snapshot", "The Saga tests.snapshot takes ModularMonolith.Tests.Sagas.ShelfSnapshotV1, a Snapshot")]
    public void A_host_whose_saga_declaration_wolverine_cannot_run_as_declared_fails_when_it_is_built(string declaration, string problem)
    {
        var refused = Assert.Throws<InvalidOperationException>(() =>
            Compose(configure: builder =>
            {
                _ = declaration switch
                {
                    "hidden" => builder.Services.AddPouryaKitSaga<HiddenSaga>("tests.hidden", "tests.hidden", saga => saga.Input<OrderPlacedV1>(placed => placed.OrderId)),
                    "unpublished" => builder.Services.AddPouryaKitSaga<UnpublishedSaga>("tests.unpublished", "tests.unpublished", saga => saga.Input<UnpublishedV1>(unpublished => unpublished.Id)),
                    "identified" => builder.Services.AddPouryaKitSaga<IdentifiedSaga>(
                        "tests.identified",
                        "tests.identified",
                        saga => saga.Input<OrderPlacedV1>(placed => placed.OrderId).Timeout<IdentifiedTimeout>()
                    ),
                    "queue" => builder.Services.AddPouryaKitSaga<QueueSaga>("billing.order-placed", "tests.queue", saga => saga.Input<OrderPlacedV1>(placed => placed.OrderId)),
                    "unowned" => builder.Services.AddPouryaKitSaga<QueueSaga>("tests.unowned", "tests.unowned", saga => saga.Input<OrderPlacedV1>(placed => placed.OrderId)),
                    "table" => builder
                        .Services.AddPouryaKitSaga<QueueSaga>("tests.twin", "tests.twin", saga => saga.Input<OrderPlacedV1>(placed => placed.OrderId))
                        .AddPouryaKitSaga<TwinSaga>("tests-twin", "tests.twin", saga => saga.Input<OrderPlacedV1>(placed => placed.OrderId)),
                    "digit" => builder.Services.AddPouryaKitSaga<QueueSaga>("2fa.enrolment", "tests.digit", saga => saga.Input<OrderPlacedV1>(placed => placed.OrderId)),
                    "input" => builder
                        .Services.Receive<PlacedV1>("order-placed.v1")
                        .AddPouryaKitSaga<QueueSaga>("orders", "tests.orders", saga => saga.Input<OrderPlacedV1>(placed => placed.OrderId))
                        .AddPouryaKitSaga<TwinSaga>("orders.orders", "tests.orders", saga => saga.Input<PlacedV1>(placed => placed.Id)),
                    _ => builder
                        .Services.ReceiveSnapshot<ShelfSnapshotV1>("tests.shelf-snapshot.v1")
                        .AddPouryaKitSaga<QueueSaga>("tests.snapshot", "tests.snapshot", saga => saga.Input<ShelfSnapshotV1>(shelf => shelf.ShelfId)),
                };
            })
        );

        Assert.Contains(problem, refused.Message, StringComparison.Ordinal);
    }

    // A saga's steps are code Wolverine generates: outside Development a Host loads it from its own assembly, where
    // codegen write put it before the Release publish (ADR 0004), and starts without it never; package validation's
    // publish of the Saga Host carries it.
    [Fact]
    public async Task A_host_with_a_saga_outside_development_does_not_start_without_its_generated_chains()
    {
        var host = Compose(environment: Environments.Production);

        var thrown = await Assert.ThrowsAsync<MissingPreBuiltTypesException>(() => host.StartAsync(Token));

        Assert.Contains("could not be loaded from the configured ApplicationAssembly", thrown.Message, StringComparison.Ordinal);
    }

    private const string SagaStepTable = "pouryakit_saga_steps";

    private static readonly Action<HostApplicationBuilder> WithProbes = builder => builder.AddPouryaKitModules(modules => modules.Add<ProbesModule>());

    private const string PaymentStep = "received_at = 'local://shipping.fulfilment/' and message_type = 'shipping.fulfilment.orders.payment-received.v1'";

    private static string HandledAt(string queue, string condition = "true") =>
        $"(select count(*) from wolverine.wolverine_incoming_envelopes where received_at = 'local://{queue}/' and status = 'Handled' and {condition})";

    private static string FulfilmentOf(string tenant, Guid order) => SagaStorage.SagaId(tenant, order.ToString());

    private static OperationContext Context(string tenant) => new(Actor.Person("saga-tests"), tenant);

    // Holds the first payment step to enter, which has loaded the saga's state, until condition holds; the returned task
    // completes when it entered. Every later entry, its retry's among them, runs on.
    private Task HoldFirstPaymentStep(string condition)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SagaProbes.InPaymentStep = async () =>
        {
            if (entered.TrySetResult())
            {
                await WaitUntilAsync(condition, seconds: 30);
            }
        };
        return entered.Task;
    }

    private async Task<string> DatabaseAsync()
    {
        var database = await broker.CreateDatabaseAsync($"sagas_{Guid.NewGuid():N}");
        await Sql.RowsAsync(database, ModuleTables);
        return database;
    }

    private IHost Compose(string schema = "wolverine", Action<HostApplicationBuilder>? configure = null, string environment = "Development")
    {
        var builder = SagaHost.Compose(["--environment", environment, $"--ConnectionStrings:app={_database}"], schema);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.UsePouryaKitTestDelivery();
        configure?.Invoke(builder);
        var host = builder.Build();
        _hosts.Add(host);
        return host;
    }

    private async Task<IHost> StartAsync(string schema = "wolverine", Action<HostApplicationBuilder>? configure = null)
    {
        var host = Compose(schema, configure);
        await host.StartAsync(Token);
        return host;
    }

    private async Task<IHost> StartBrokerHostAsync(string database, Uri brokerUri, string serviceName, Action<HostApplicationBuilder> modules)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            ["--environment", Environments.Development, $"--ConnectionStrings:app={database}", $"--ConnectionStrings:broker={brokerUri}"]
        );
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        modules(builder);
        builder.UsePouryaKitWolverine(connectionName: "app");
        builder.UsePouryaKitRabbitMq(connectionName: "broker", serviceName: serviceName);
        builder.Services.UsePouryaKitTestDelivery();
        var host = builder.Build();
        _hosts.Add(host);
        await host.StartAsync(Token);
        return host;
    }

    private static Task PlaceAsync(IHost host, string tenant, Guid order, int installments = 2, int payWithinSeconds = 3600) =>
        PlaceAsync(host, Context(tenant), order, installments, payWithinSeconds);

    private static async Task PlaceAsync(IHost host, OperationContext context, Guid order, int installments = 2, int payWithinSeconds = 3600)
    {
        var placed = await host
            .Services.GetRequiredService<IGatewayProcessor<OrdersModule>>()
            .SendAsync(new PlaceOrderCommand(order, installments, payWithinSeconds), context, Token);
        Assert.True(placed.IsSuccess);
    }

    private static Task PayAsync(IHost host, string tenant, Guid order) => PayAsync(host, Context(tenant), order);

    private static async Task PayAsync(IHost host, OperationContext context, Guid order)
    {
        var paid = await host.Services.GetRequiredService<IGatewayProcessor<OrdersModule>>().SendAsync(new RecordPaymentCommand(order), context, Token);
        Assert.True(paid.IsSuccess);
    }

    private async Task StartedAsync(IHost host, Guid order, int installments = 3, int payWithinSeconds = 3600)
    {
        await PlaceAsync(host, "tenant-a", order, installments, payWithinSeconds);
        await WaitUntilAsync($"exists (select from wolverine.shipping_fulfilment_saga where id = '{FulfilmentOf("tenant-a", order)}')");
    }

    // As the Kit's Outbox writes it for an Operation of the tenant: its Operation Context headers and the identity
    // eventId, under a new envelope id.
    private static async Task PublishAsync(IHost host, object integrationEvent, Guid eventId, string? tenant)
    {
        var options = new DeliveryOptions().WithOperationContext(new OperationContext(Actor.Person("saga-tests"), tenant));
        options.Headers[IntegrationEventHeaders.DomainEventId] = eventId.ToString();
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().PublishAsync(integrationEvent, options);
    }

    private async Task<Process> ChildAsync(string line, Guid order, params string[] options)
    {
        var child = Process.Start(
            new ProcessStartInfo("dotnet", [Path.Combine(AppContext.BaseDirectory, "ModularMonolith.Tests.SagaChild.dll"), "pay", _database, "tenant-a", order.ToString(), .. options])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        )!;
        child.ErrorDataReceived += (_, _) => { };
        child.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        string? printed;
        do
        {
            printed = await child.StandardOutput.ReadLineAsync(timeout.Token);
        } while (printed is not null && printed != line);

        Assert.Equal(line, printed);
        return child;
    }

    private async Task<JsonElement?> SagaAsync(string table, string sagaId, string schema = "wolverine")
    {
        var rows = await RowsAsync($"select body::text from {schema}.{table}_saga where id = '{sagaId}'");
        return rows.Count == 0 ? null : JsonDocument.Parse((string)rows[0][0]).RootElement.Clone();
    }

    private async Task<int?> PaidAsync(string tenant, Guid order) => (await SagaAsync("shipping_fulfilment", FulfilmentOf(tenant, order)))?.GetProperty("Paid").GetInt32();

    private async Task<Guid> ClaimedAsync(string sagaId, string wireName, string saga = Fulfilment) =>
        (Guid)Assert.Single(await RowsAsync($"select event_id from wolverine.{SagaStepTable} where saga = '{saga}' and saga_id = '{sagaId}' and input = '{wireName}'"))[0];

    private async Task<string[]> SchemasOfAsync(string table) =>
        [.. (await RowsAsync($"select table_schema from information_schema.tables where table_name = '{table}'")).Select(row => (string)row[0])];

    // No delivery pending: every stored envelope handled or set aside, no retry waiting, only a timeout scheduled.
    private Task SettledAsync() =>
        WaitUntilAsync("(select count(*) from wolverine.wolverine_incoming_envelopes where status <> 'Handled' and status <> 'Scheduled') = 0");

    private Task<List<object[]>> RowsAsync(string sql) => Sql.RowsAsync(_database, sql);

    private async Task<long> CountAsync(string sql) => (long)(await RowsAsync(sql))[0][0];

    private Task WaitUntilAsync(string condition, int seconds = 15) => Sql.WaitUntilAsync(_database, condition, seconds: seconds);

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            try
            {
                await host.StopAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                // A Host that never started, or failed to, has nothing to stop.
            }

            host.Dispose();
        }
    }
}

// The declarations the Host's build refuses: a saga Wolverine cannot name, an input with no wire name, a timeout that
// names an identity member, a saga whose queue a subscription has.
internal sealed class HiddenSaga : PouryaKitSaga
{
    public static HiddenSaga Start(SagaInput<HiddenSaga, OrderPlacedV1> placed) => new() { Id = placed.SagaId };
}

public sealed record UnpublishedV1(Guid Id);

public sealed class UnpublishedSaga : PouryaKitSaga
{
    public static UnpublishedSaga Start(SagaInput<UnpublishedSaga, UnpublishedV1> created) => new() { Id = created.SagaId };
}

public sealed record IdentifiedTimeout(TimeSpan Within, string SagaId) : SagaTimeout(Within);

public sealed class IdentifiedSaga : PouryaKitSaga
{
    public static IdentifiedSaga Start(SagaInput<IdentifiedSaga, OrderPlacedV1> placed) => new() { Id = placed.SagaId };

    public void Handle(IdentifiedTimeout timeout) => ArgumentNullException.ThrowIfNull(timeout);
}

public sealed class QueueSaga : PouryaKitSaga
{
    public static QueueSaga Start(SagaInput<QueueSaga, OrderPlacedV1> placed) => new() { Id = placed.SagaId };
}

public sealed record PlacedV1(Guid Id);

// Two sagas of one simple name in two namespaces, declared by a Module of their own.
public sealed class TwinsModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module)
    {
        module.AddPouryaKitSaga<TwinSaga>("tests.twin", "tests.twin", saga => saga.Input<ReferenceOpenedV1>(opened => opened.Reference));
        module.AddPouryaKitSaga<Twins.TwinSaga>("tests.other-twin", "tests.other-twin", saga => saga.Input<ReferenceOpenedV1>(opened => opened.Reference));
    }
}

public sealed class TwinSaga : PouryaKitSaga
{
    public string Twin { get; set; } = "";

    public static TwinSaga Start(SagaInput<TwinSaga, ReferenceOpenedV1> opened)
    {
        ArgumentNullException.ThrowIfNull(opened);
        return new() { Twin = "this" };
    }
}
