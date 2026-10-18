using System.Text.Json;
using PouryaKit.Cqrs;
using SagaSpike.Billing;
using SagaSpike.Contracts;
using SagaSpike.Host;
using SagaSpike.Kit;
using SagaSpike.Shipping;
using Wolverine.Util;

namespace SagaSpike.Tests;

// Item 5: what a saga emits, a timeout's step included, reaches an ordinary subscription of a tenant-scoped Module,
// Billing, through the Kit's adapter: under the saga's declared system Actor, the starting event's tenant and correlation,
// the step's identity as causation, and an identity every delivery of the step repeats, which Billing's Receipt keys.
public sealed class EmissionTests(Postgres postgres) : SpikeTest(postgres)
{
    [Fact]
    public async Task A_completing_steps_emission_reaches_a_tenant_scoped_subscription_under_the_sagas_actor_and_the_starting_events_tenant_and_correlation()
    {
        var host = await StartAsync();
        var order = Guid.CreateVersion7();
        var (placing, paying) = (Context("tenant-a"), Context("tenant-a"));
        await PlaceAsync(host, placing, order, installments: 1);
        await WaitUntilAsync("select count(*) = 1 from wolverine.fulfilmentsaga_saga where id = $1", $"tenant-a:{order}");

        await PayAsync(host, paying, order);

        await WaitUntilAsync("select count(*) = 1 from billing.pouryakit_receipts where subscription = 'billing.order-fulfilled'");
        var step = SagaStorage.StepId(Fulfilment, $"tenant-a:{order}", "orders.payment-received.v1", await ClaimedAsync($"tenant-a:{order}", "orders.payment-received.v1"));
        var fulfilled = Assert.Single(BillingLog.Of(order), delivery => delivery.Subscription == "billing.order-fulfilled").Context;
        Assert.Equal(
            (ActorKind.System, "shipping.fulfilment", "tenant-a", placing.CorrelationId, step),
            (fulfilled.Actor.Kind, fulfilled.Actor.Id, fulfilled.Tenant, fulfilled.CorrelationId, fulfilled.CausationId)
        );
        Assert.NotEqual(paying.CorrelationId, fulfilled.CorrelationId);
        Assert.Equal(
            SagaStorage.EmissionId(step, "shipping.order-fulfilled.v1"),
            await Db.ScalarAsync<Guid>(ConnectionString, "select message_id from billing.pouryakit_receipts where subscription = 'billing.order-fulfilled'")
        );
    }

    // A timeout carries no Operation Context headers: what its step emits takes the tenant and correlation the saga state
    // kept from its starting event, and a stable identity the state kept for the timeout when the starting step scheduled
    // it, derived from that step's identity, not from the timeout's envelope.
    [Fact]
    public async Task A_timeouts_emission_reaches_the_subscription_with_the_identity_tenant_and_correlation_kept_in_the_saga_state()
    {
        var host = await StartAsync();
        var order = Guid.CreateVersion7();
        var placing = Context("tenant-a");
        var sagaId = $"tenant-a:{order}";
        var timeoutType = typeof(SagaSpike.Shipping.FulfilmentTimeout).FullName!;

        await PlaceAsync(host, placing, order, installments: 2, payWithinSeconds: 3);

        await WaitUntilAsync("select count(*) = 1 from wolverine.fulfilmentsaga_saga where id = $1", sagaId);
        var scheduling = SagaStorage.StepId(Fulfilment, sagaId, "orders.order-placed.v1", await ClaimedAsync(sagaId, "orders.order-placed.v1"));
        var timeout = SagaStorage.TimeoutId(scheduling, timeoutType, 0);
        var envelope = await Db.ScalarAsync<Guid>(ConnectionString, "select id from wolverine.wolverine_incoming_envelopes where message_type = $1", timeoutType);
        var kept = (await SagaAsync(sagaId))!.Value.GetProperty("Timeouts");
        Assert.Equal([(envelope.ToString(), timeout.ToString())], kept.EnumerateObject().Select(entry => (entry.Name, entry.Value.GetString())));
        Assert.NotEqual(envelope, timeout);

        await WaitUntilAsync("select count(*) = 1 from billing.pouryakit_receipts where subscription = 'billing.order-cancelled'");
        Assert.Null(await SagaAsync(sagaId));
        Assert.Equal(timeout, await ClaimedAsync(sagaId, timeoutType));
        var step = SagaStorage.StepId(Fulfilment, sagaId, timeoutType, timeout);
        var cancelled = Assert.Single(BillingLog.Of(order), delivery => delivery.Subscription == "billing.order-cancelled").Context;
        Assert.Equal(
            (ActorKind.System, "shipping.fulfilment", "tenant-a", placing.CorrelationId, step),
            (cancelled.Actor.Kind, cancelled.Actor.Id, cancelled.Tenant, cancelled.CorrelationId, cancelled.CausationId)
        );
        Assert.Equal(
            SagaStorage.EmissionId(step, "shipping.order-cancelled.v1"),
            await Db.ScalarAsync<Guid>(ConnectionString, "select message_id from billing.pouryakit_receipts where subscription = 'billing.order-cancelled'")
        );
    }

    // One step schedules two timeouts of one type: each keeps an identity of its own, from its place among the step's
    // timeouts of that type, so each claims its own step and both apply.
    [Fact]
    public async Task Two_timeouts_of_one_type_from_one_step_each_keep_their_own_identity_and_both_apply()
    {
        var host = await StartAsync(
            new SpikeHostOptions
            {
                Services = services => services.AddSpikeSaga<ReminderSaga>("shipping.reminder", "shipping.reminder", saga => saga.Input<ReferenceOpenedV1>(opened => opened.Reference).Timeout<ReminderTimeout>()),
            }
        );
        var sagaId = SagaStorage.SagaId("tenant-a", "r-1");
        var (openedType, timeoutType) = (typeof(ReferenceOpenedV1).ToMessageTypeName(), typeof(ReminderTimeout).FullName!);

        await PublishAsync(host, new ReferenceOpenedV1("r-1"), Guid.CreateVersion7(), Context("tenant-a"));

        await WaitUntilAsync("select count(*) = 1 from wolverine.remindersaga_saga where id = $1", sagaId);
        await SettledAsync();
        var state = JsonDocument.Parse(await Db.ScalarAsync<string>(ConnectionString, "select body::text from wolverine.remindersaga_saga where id = $1", sagaId)).RootElement;
        Assert.Equal([1, 2], state.GetProperty("Reminded").EnumerateArray().Select(delay => delay.GetInt32()).Order());
        var scheduling = SagaStorage.StepId("shipping.reminder", sagaId, openedType, await ClaimedAsync(sagaId, openedType));
        Guid[] identities = [.. new[] { SagaStorage.TimeoutId(scheduling, timeoutType, 0), SagaStorage.TimeoutId(scheduling, timeoutType, 1) }.Order()];
        Assert.Equal(identities, state.GetProperty("Timeouts").EnumerateObject().Select(entry => entry.Value.GetGuid()).Order());
        Assert.Equal(
            identities,
            (await Db.RowsAsync(ConnectionString, "select event_id from wolverine.pouryakit_saga_steps where saga_id = $1 and input = $2", sagaId, timeoutType))
                .Select(row => (Guid)row[0]!)
                .Order()
        );
        Assert.Equal(0, await CountAsync("select count(*) from wolverine.wolverine_dead_letters"));
    }

    // The rejected option, one honest attempt: the saga publishes as Wolverine publishes from a saga, which carries
    // Wolverine's own correlation and saga id but none of the Kit's Operation Context headers or the event's identity.
    [Fact]
    public async Task Without_the_kits_adapter_a_sagas_emission_is_set_aside_by_an_ordinary_subscription()
    {
        var host = await StartAsync(new SpikeHostOptions { Sagas = sagas => sagas.KitEmissions = false });
        var order = Guid.CreateVersion7();
        await PlaceAsync(host, Context("tenant-a"), order, installments: 1);
        await WaitUntilAsync("select count(*) = 1 from wolverine.fulfilmentsaga_saga where id = $1", $"tenant-a:{order}");

        await PayAsync(host, Context("tenant-a"), order);

        await WaitUntilAsync("select count(*) = 1 from wolverine.wolverine_dead_letters where received_at = 'local://billing.order-fulfilled/'");
        Assert.Contains(
            "has no pouryakit-actor-kind header",
            await Db.ScalarAsync<string>(ConnectionString, "select exception_message from wolverine.wolverine_dead_letters where received_at = 'local://billing.order-fulfilled/'")
        );
        Assert.DoesNotContain(BillingLog.Of(order), delivery => delivery.Subscription == "billing.order-fulfilled");
    }
}
