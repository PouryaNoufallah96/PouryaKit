using SagaSpike.Contracts;

namespace SagaSpike.Tests;

// Item 4: a saga's identity is the tenant with the business id, which the Kit's route takes from the Operation Context
// headers the Outbox wrote. The events here are published as the Outbox writes them: two tenants' Modules may each own
// an order of one id, which Orders' own table, keyed by the order alone, does not allow.
public sealed class TenancyTests(Postgres postgres) : SpikeTest(postgres)
{
    [Fact]
    public async Task One_order_id_in_two_tenants_is_two_sagas_and_a_payment_advances_only_its_own_tenants()
    {
        var host = await StartAsync();
        var order = Guid.CreateVersion7();

        await PublishAsync(host, new OrderPlacedV1(order, 2, 3600), Guid.CreateVersion7(), Context("tenant-a"));
        await PublishAsync(host, new OrderPlacedV1(order, 3, 3600), Guid.CreateVersion7(), Context("tenant-b"));
        await WaitUntilAsync("select count(*) = 2 from wolverine.fulfilmentsaga_saga where body->>'Order' = $1", order.ToString());
        await PublishAsync(host, new PaymentReceivedV1(order), Guid.CreateVersion7(), Context("tenant-b"));

        await WaitUntilAsync("select (body->>'Paid')::int = 1 from wolverine.fulfilmentsaga_saga where id = $1", $"tenant-b:{order}");
        await SettledAsync();
        var a = (await SagaAsync($"tenant-a:{order}"))!.Value;
        Assert.Equal(("tenant-a", 2, 0), (a.GetProperty("Tenant").GetString(), a.GetProperty("Installments").GetInt32(), a.GetProperty("Paid").GetInt32()));
        Assert.Equal("tenant-b", (await SagaAsync($"tenant-b:{order}"))!.Value.GetProperty("Tenant").GetString());
        Assert.Equal(0, await CountAsync("select count(*) from wolverine.wolverine_dead_letters"));
    }

    // The payment of another tenant reaches no saga: its identity names its own tenant, where no saga exists, so Wolverine
    // finds none, the step fails before it runs, and the delivery is set aside after its retries.
    [Fact]
    public async Task A_payment_of_another_tenant_changes_no_saga_and_emits_nothing()
    {
        var host = await StartAsync();
        var order = Guid.CreateVersion7();
        await PublishAsync(host, new OrderPlacedV1(order, 1, 3600), Guid.CreateVersion7(), Context("tenant-a"));
        await WaitUntilAsync("select count(*) = 1 from wolverine.fulfilmentsaga_saga where body->>'Order' = $1", order.ToString());
        var payment = Guid.CreateVersion7();

        await PublishAsync(host, new PaymentReceivedV1(order), payment, Context("tenant-c"));

        await SettledAsync();
        Assert.Equal(
            "Wolverine.Persistence.Sagas.UnknownSagaException",
            await Db.ScalarAsync<string>(ConnectionString, "select exception_type from wolverine.wolverine_dead_letters where received_at = 'local://shipping.fulfilment/'")
        );
        Assert.Equal(0, await Db.ScalarAsync<int>(ConnectionString, "select (body->>'Paid')::int from wolverine.fulfilmentsaga_saga where body->>'Order' = $1", order.ToString()));
        Assert.Equal(0, await CountAsync("select count(*) from wolverine.pouryakit_saga_steps where event_id = $1", payment));
        Assert.Equal(0, await CountAsync("select count(*) from wolverine.wolverine_incoming_envelopes where received_at = 'local://billing.order-fulfilled/'"));
    }

    // Refused before any step: an event whose Operation has no tenant.
    [Fact]
    public async Task An_event_with_no_tenant_is_refused_before_any_step()
    {
        var host = await StartAsync();
        var order = Guid.CreateVersion7();

        await PublishAsync(host, new OrderPlacedV1(order, 1, 3600), Guid.CreateVersion7(), new PouryaKit.Cqrs.OperationContext(PouryaKit.Cqrs.Actor.Person("spike-tests")));

        await WaitUntilAsync("select count(*) = 1 from wolverine.wolverine_dead_letters where received_at = 'local://shipping.fulfilment/'");
        Assert.StartsWith(
            "Tenant.Required",
            await Db.ScalarAsync<string>(ConnectionString, "select exception_message from wolverine.wolverine_dead_letters where received_at = 'local://shipping.fulfilment/'")
        );
        // No step ever ran in this database: Wolverine creates the saga's table at its first, the Kit its claims' table.
        Assert.Equal(0, await CountAsync("select count(*) from information_schema.tables where table_name in ('fulfilmentsaga_saga', 'pouryakit_saga_steps')"));
    }
}
