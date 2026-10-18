using Microsoft.Extensions.DependencyInjection;
using SagaSpike.Billing;
using SagaSpike.Contracts;
using SagaSpike.Host;
using SagaSpike.Shipping;
using Wolverine;

namespace SagaSpike.Tests;

// Item 1: a Saga and a Kit subscription on one message type stay separate. Billing subscribes to OrderPlacedV1 through
// the Kit, and the fulfilment saga starts from it through the Kit's route; Orders publishes it from a Notification Handler,
// the Kit's own path.
public sealed class SeparationTests(Postgres postgres) : SpikeTest(postgres)
{
    [Fact]
    public async Task A_saga_and_a_kit_subscription_on_one_integration_event_each_receive_it_once()
    {
        var host = await StartAsync();
        var order = Guid.CreateVersion7();

        await PlaceAsync(host, Context("tenant-a"), order, installments: 3);

        await WaitUntilAsync("select count(*) = 1 from wolverine.fulfilmentsaga_saga where id = $1", $"tenant-a:{order}");
        await SettledAsync();
        Assert.Equal(3, (await SagaAsync($"tenant-a:{order}"))!.Value.GetProperty("Installments").GetInt32());
        Assert.Equal(1, await CountAsync("select count(*) from billing.pouryakit_receipts where subscription = 'billing.order-placed'"));
        Assert.Equal("billing.order-placed", Assert.Single(BillingLog.Of(order)).Subscription);
        Assert.Equal(0, await CountAsync("select count(*) from wolverine.wolverine_dead_letters"));
    }

    // The rejected route, one honest attempt: a plain Wolverine saga started by the event itself, routed to its own queue.
    // The Kit's pre-built delivery handler of the type is the handler on every queue (HandlerGraph.HandlerFor reads it
    // before any generated chain, and one saga is never a sticky handler), so the saga's queue gets the Kit's handler,
    // which finds no subscription of that name.
    [Fact]
    public async Task Routed_as_itself_an_integration_event_a_kit_subscription_handles_never_reaches_a_wolverine_saga()
    {
        // In an extension, as the Kit's routes are, so the route names the type after the Kit declared its wire name.
        var host = await StartAsync(new SpikeHostOptions { Services = services => services.AddSingleton<IWolverineExtension, NaiveRoute>() });
        var order = Guid.CreateVersion7();

        await PlaceAsync(host, Context("tenant-a"), order);

        await WaitUntilAsync("select count(*) = 1 from wolverine.wolverine_dead_letters where received_at = 'local://shipping.naive/'");
        Assert.Equal(
            "PouryaKit.Messaging.Wolverine.UnknownSubscriptionException",
            await Db.ScalarAsync<string>(ConnectionString, "select exception_type from wolverine.wolverine_dead_letters where received_at = 'local://shipping.naive/'")
        );
        Assert.Equal(0, await CountAsync("select count(*) from information_schema.tables where table_name = 'naivesaga_saga'"));
        Assert.Equal(1, await CountAsync("select count(*) from billing.pouryakit_receipts where subscription = 'billing.order-placed'"));
    }
}

/// <summary>The rejected route: the naive saga, and its start event routed to the saga's own queue.</summary>
public sealed class NaiveRoute : IWolverineExtension
{
    /// <inheritdoc />
    public void Configure(WolverineOptions options)
    {
        options.Discovery.IncludeType<NaiveSaga>();
        options.PublishMessage<OrderPlacedV1>().ToLocalQueue("shipping.naive");
    }
}
