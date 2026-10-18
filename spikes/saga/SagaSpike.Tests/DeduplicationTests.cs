using SagaSpike.Contracts;
using SagaSpike.Host;
using SagaSpike.Kit;

namespace SagaSpike.Tests;

// Item 3: the Kit's claim, keyed by the saga's name, the input's wire name and the event's identity and written in the
// step's transaction before Wolverine loads the saga, makes one event delivered under two envelope ids one step, while two
// contracts Orders publishes for one Domain Event are two. The claims outlive the saga.
public sealed class DeduplicationTests(Postgres postgres) : SpikeTest(postgres)
{
    // A payment delivered again under a new envelope id, as a broker's fan-out after a crash or a republish delivers it.
    [Fact]
    public async Task One_payment_delivered_under_two_envelope_ids_advances_the_saga_once()
    {
        var host = await StartAsync();
        var (order, sagaId) = (Guid.CreateVersion7(), "");
        sagaId = await StartedAsync(host, order);
        var payment = Guid.CreateVersion7();

        await PublishAsync(host, new PaymentReceivedV1(order), payment, Context("tenant-a"));
        await PublishAsync(host, new PaymentReceivedV1(order), payment, Context("tenant-a"));

        await WaitUntilAsync("select count(*) = 2 from wolverine.wolverine_incoming_envelopes where message_type = 'SagaInputFulfilmentSaga_PaymentReceivedV1' and status = 'Handled'");
        Assert.Equal(1, (await SagaAsync(sagaId))!.Value.GetProperty("Paid").GetInt32());
        Assert.Equal(1, await CountAsync("select count(*) from wolverine.pouryakit_saga_steps where event_id = $1", payment));
    }

    // The rejected option, one honest attempt: the claim keyed by the delivery's envelope id. A redelivery of one envelope
    // repeats its id, but a second delivery of the event does not.
    [Fact]
    public async Task Keyed_by_its_envelope_id_one_payment_under_two_envelope_ids_advances_the_saga_twice()
    {
        var host = await StartAsync(new SpikeHostOptions { Sagas = sagas => sagas.ClaimKey = SagaClaimKey.EnvelopeId });
        var order = Guid.CreateVersion7();
        var sagaId = await StartedAsync(host, order);
        var payment = Guid.CreateVersion7();

        await PublishAsync(host, new PaymentReceivedV1(order), payment, Context("tenant-a"));
        await PublishAsync(host, new PaymentReceivedV1(order), payment, Context("tenant-a"));

        await WaitUntilAsync("select (body->>'Paid')::int = 2 from wolverine.fulfilmentsaga_saga where id = $1", sagaId);
        Assert.Equal(2, await CountAsync("select count(*) from wolverine.pouryakit_saga_steps where input = 'orders.payment-received.v1'"));
    }

    // Orders' one Notification Handler publishes both payment contracts for one Domain Event, under its one identity.
    [Fact]
    public async Task Two_contracts_published_for_one_payment_both_reach_the_saga()
    {
        var host = await StartAsync();
        var order = Guid.CreateVersion7();
        var sagaId = await StartedAsync(host, order);

        await PayAsync(host, Context("tenant-a"), order);

        await WaitUntilAsync("select (body->>'Paid')::int = 1 and body->'Methods' = '[\"card\"]'::jsonb from wolverine.fulfilmentsaga_saga where id = $1", sagaId);
        Assert.Equal(
            await ClaimedAsync(sagaId, "orders.payment-received.v1"),
            await ClaimedAsync(sagaId, "orders.payment-received.v2")
        );
    }

    // Kept until the saga completes, and after: a late copy of the starting event finds its claim and starts nothing.
    [Fact]
    public async Task A_copy_of_the_starting_event_delivered_after_the_saga_completed_starts_no_saga()
    {
        var host = await StartAsync();
        var order = Guid.CreateVersion7();
        var sagaId = await StartedAsync(host, order, installments: 1);
        var placed = await ClaimedAsync(sagaId, "orders.order-placed.v1");
        await PayAsync(host, Context("tenant-a"), order);
        await WaitUntilAsync("select count(*) = 1 from billing.pouryakit_receipts where subscription = 'billing.order-fulfilled'");
        Assert.Null(await SagaAsync(sagaId));

        await PublishAsync(host, new OrderPlacedV1(order, 1, 3600), placed, Context("tenant-a"));

        await WaitUntilAsync("select count(*) = 2 from wolverine.wolverine_incoming_envelopes where message_type = 'SagaInputFulfilmentSaga_OrderPlacedV1' and status = 'Handled'");
        Assert.Null(await SagaAsync(sagaId));
        Assert.Equal(1, await CountAsync("select count(*) from billing.pouryakit_receipts where subscription = 'billing.order-fulfilled'"));
    }

    private async Task<string> StartedAsync(Microsoft.Extensions.Hosting.IHost host, Guid order, int installments = 3)
    {
        var sagaId = $"tenant-a:{order}";
        await PlaceAsync(host, Context("tenant-a"), order, installments);
        await WaitUntilAsync("select count(*) = 1 from wolverine.fulfilmentsaga_saga where id = $1", sagaId);
        return sagaId;
    }
}
