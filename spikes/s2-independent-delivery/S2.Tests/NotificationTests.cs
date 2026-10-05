using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using S2.Catalog;
using S2.Host;
using S2.Kit.Harness;

namespace S2.Tests;

/// <summary>
/// Notifications in their normal shape: Catalog raises an internal Domain Event, and two internal Notification Handlers
/// in Catalog react to it. Index fails twice; Announce succeeds at once.
/// </summary>
public sealed class NotificationTests(PostgresServer server)
{
    /// <summary>Options that run no generated code deliver the internal Domain Event, and retry the two handlers independently.</summary>
    [Theory]
    [InlineData(DeliveryChoice.RoutedQueues)]
    [InlineData(DeliveryChoice.DeliveryEnvelopePrebuilt)]
    public async Task Two_notification_handlers_of_an_internal_domain_event_are_retried_independently(DeliveryChoice delivery)
    {
        await using var db = await server.CreateDatabaseAsync();
        var productId = Guid.NewGuid();
        HandlerScript.Set(CatalogNotifications.IndexHandler, productId, new Plan(FailureKind.FailedResult, Failures: 2));
        using var host = await StartAsync(db, delivery);

        await RaiseAsync(host, productId);

        Assert.True(await Eventually.WaitAsync(async () =>
            (await db.InvocationsAsync(CatalogNotifications.IndexHandler, productId)).Contains("Succeeded"), TimeSpan.FromSeconds(15)));
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(["FailedResult", "FailedResult", "Succeeded"], await db.InvocationsAsync(CatalogNotifications.IndexHandler, productId));
        Assert.Equal(["Succeeded"], await db.InvocationsAsync(CatalogNotifications.AnnounceHandler, productId));
        Assert.Empty(await db.DeadLettersAsync());
        var rows = await db.IncomingAsync();
        Assert.Equal([CatalogNotifications.AnnounceSubscription, CatalogNotifications.IndexSubscription], rows.Select(r => r.Subscription));
        Assert.All(rows, r => Assert.Equal("Handled", r.Status));
    }

    /// <summary>
    /// Options whose Wolverine handler is generated code closed over the payload type cannot compile it when the
    /// payload is an internal Domain Event: no handler runs, and the Notification is dead-lettered with CS0122.
    /// </summary>
    [Theory]
    [InlineData(DeliveryChoice.DeliveryEnvelope)]
    [InlineData(DeliveryChoice.SeparatedSlots)]
    public async Task Generated_adapters_cannot_deliver_an_internal_domain_event(DeliveryChoice delivery)
    {
        await using var db = await server.CreateDatabaseAsync();
        var productId = Guid.NewGuid();
        using var host = await StartAsync(db, delivery);

        await RaiseAsync(host, productId);

        Assert.True(await Eventually.WaitAsync(async () => (await db.DeadLettersAsync()).Count == 2, TimeSpan.FromSeconds(15)));
        // Option B's two deliveries share one chain: one failed with CS0122, the other sometimes with a code generation
        // error ("Frame chain is being re-arranged") from compiling the same chain again concurrently.
        Assert.Contains(await db.DeadLettersAsync(), d => d.ExceptionMessage!.Contains("CS0122: 'ProductCreated' is inaccessible"));
        Assert.Empty(await db.InvocationsAsync(CatalogNotifications.IndexHandler, productId));
        Assert.Empty(await db.InvocationsAsync(CatalogNotifications.AnnounceHandler, productId));
    }

    private static Task<IHost> StartAsync(SpikeDatabase db, DeliveryChoice delivery) =>
        db.StartHostAsync(new SpikeSettings(db.ConnectionString, delivery,
            InventorySubscribes: false, BillingSubscribes: false, CatalogNotifications: true));

    private static async Task RaiseAsync(IHost host, Guid productId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await CatalogNotifications.RaiseProductCreatedAsync(scope.ServiceProvider.GetRequiredService<S2.Kit.UnitOfWork<CatalogDbContext>>(), productId);
    }
}
