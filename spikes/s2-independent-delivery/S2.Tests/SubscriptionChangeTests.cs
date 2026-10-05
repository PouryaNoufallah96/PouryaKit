using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using S2.Catalog;
using S2.Host;
using S2.Inventory;
using S2.Kit.Harness;
using Wolverine;

namespace S2.Tests;

/// <summary>
/// Option C. A delivery is left pending: its handler fails and waits a minute for its retry when the Host stops
/// gracefully (row <c>Incoming</c>, owner 0). The next deployment changes the subscriptions.
/// </summary>
public sealed class SubscriptionChangeTests(PostgresServer server)
{
    private static readonly TimeSpan[] LongRetry = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)];

    /// <summary>
    /// No subscription to the Integration Event type is left, so Wolverine has no handler for it. With its default
    /// <c>UnknownMessageBehavior.LogOnly</c> it marks the row Handled: the committed delivery is discarded, no handler
    /// runs and nothing is dead-lettered.
    /// </summary>
    [Fact]
    public async Task A_pending_delivery_whose_type_lost_every_subscription_is_discarded_by_default()
    {
        await using var db = await server.CreateDatabaseAsync();
        var productId = Guid.NewGuid();
        var pending = await LeaveInventoryDeliveryPendingAsync(db, productId);

        using var next = await db.StartHostAsync(new SpikeSettings(db.ConnectionString, DeliveryChoice.RoutedQueues,
            InventorySubscribes: false, BillingSubscribes: false));

        Assert.True(await Eventually.WaitAsync(async () =>
            (await db.IncomingAsync()).Single(e => e.Id == pending.Id).Status == "Handled", TimeSpan.FromSeconds(15)));
        Assert.Empty(await db.DeadLettersAsync());
        Assert.Equal(["FailedResult"], await db.InvocationsAsync(InventoryModule.HandlerName, productId));
    }

    /// <summary>The remedy: with <c>UnknownMessageBehavior.DeadLetterQueue</c> the same delivery is dead-lettered, never Handled.</summary>
    [Fact]
    public async Task A_pending_delivery_whose_type_lost_every_subscription_is_dead_lettered_when_unknown_messages_go_to_the_dead_letter_queue()
    {
        await using var db = await server.CreateDatabaseAsync();
        var productId = Guid.NewGuid();
        var pending = await LeaveInventoryDeliveryPendingAsync(db, productId);

        using var next = await db.StartHostAsync(new SpikeSettings(db.ConnectionString, DeliveryChoice.RoutedQueues,
            InventorySubscribes: false, BillingSubscribes: false, UnknownMessages: UnknownMessageBehavior.DeadLetterQueue));

        Assert.True(await Eventually.WaitAsync(async () => (await db.DeadLettersAsync()).Count == 1, TimeSpan.FromSeconds(15)));
        var deadLetter = Assert.Single(await db.DeadLettersAsync());
        Assert.Equal((pending.Id, pending.ReceivedAt, "Wolverine.Runtime.Interop.UnknownMessageTypeNameException"),
            (deadLetter.Id, deadLetter.ReceivedAt, deadLetter.ExceptionType));
        Assert.DoesNotContain(await db.IncomingAsync(), e => e.Id == pending.Id && e.Status == "Handled");
        Assert.Equal(["FailedResult"], await db.InvocationsAsync(InventoryModule.HandlerName, productId));
    }

    /// <summary>
    /// The subscription is renamed. The row on the old queue is recovered and handed to the routed handler, whose name
    /// lookup throws <see cref="KeyNotFoundException"/>; the retry ladder runs and the row is dead-lettered. The renamed
    /// subscription never receives it.
    /// </summary>
    [Fact]
    public async Task A_pending_delivery_whose_subscription_was_renamed_is_dead_lettered()
    {
        await using var db = await server.CreateDatabaseAsync();
        var productId = Guid.NewGuid();
        var pending = await LeaveInventoryDeliveryPendingAsync(db, productId);

        using var next = await db.StartHostAsync(new SpikeSettings(db.ConnectionString, DeliveryChoice.RoutedQueues,
            BillingSubscribes: false, InventorySubscription: "inventory.product-created-renamed"));

        Assert.True(await Eventually.WaitAsync(async () => (await db.DeadLettersAsync()).Count == 1, TimeSpan.FromSeconds(15)));
        var deadLetter = Assert.Single(await db.DeadLettersAsync());
        Assert.Equal((pending.Id, pending.ReceivedAt, typeof(KeyNotFoundException).FullName),
            (deadLetter.Id, deadLetter.ReceivedAt, deadLetter.ExceptionType));
        Assert.Equal(["FailedResult"], await db.InvocationsAsync(InventoryModule.HandlerName, productId));
    }

    /// <summary>The same graceful stop with an internal Domain Event pending for an internal Notification Handler: the next Host delivers it.</summary>
    [Fact]
    public async Task A_pending_internal_domain_event_is_delivered_by_the_next_host()
    {
        await using var db = await server.CreateDatabaseAsync();
        var productId = Guid.NewGuid();
        HandlerScript.Set(CatalogNotifications.IndexHandler, productId, Plan.FailForever());
        var settings = new SpikeSettings(db.ConnectionString, DeliveryChoice.RoutedQueues,
            InventorySubscribes: false, BillingSubscribes: false, CatalogNotifications: true, RetryCooldowns: LongRetry);

        using (var first = await db.StartHostAsync(settings))
        {
            await using (var scope = first.Services.CreateAsyncScope())
                await CatalogNotifications.RaiseProductCreatedAsync(scope.ServiceProvider.GetRequiredService<S2.Kit.UnitOfWork<CatalogDbContext>>(), productId);
            Assert.True(await Eventually.WaitAsync(async () =>
                (await db.InvocationsAsync(CatalogNotifications.IndexHandler, productId)).Count == 1 &&
                (await db.InvocationsAsync(CatalogNotifications.AnnounceHandler, productId)).Count == 1, TimeSpan.FromSeconds(15)));
            await first.StopAsync();
        }

        HandlerScript.Set(CatalogNotifications.IndexHandler, productId, new Plan(FailureKind.FailedResult, 0));
        using var next = await db.StartHostAsync(settings);

        Assert.True(await Eventually.WaitAsync(async () =>
            (await db.InvocationsAsync(CatalogNotifications.IndexHandler, productId)).Contains("Succeeded"), TimeSpan.FromSeconds(20)));
        Assert.Equal(["FailedResult", "Succeeded"], await db.InvocationsAsync(CatalogNotifications.IndexHandler, productId));
        Assert.Equal(["Succeeded"], await db.InvocationsAsync(CatalogNotifications.AnnounceHandler, productId));
    }

    private static async Task<EnvelopeRow> LeaveInventoryDeliveryPendingAsync(SpikeDatabase db, Guid productId)
    {
        HandlerScript.Set(InventoryModule.HandlerName, productId, Plan.FailForever());
        using (var first = await db.StartHostAsync(new SpikeSettings(db.ConnectionString, DeliveryChoice.RoutedQueues,
                   BillingSubscribes: false, RetryCooldowns: LongRetry)))
        {
            await SpikeDatabase.PublishAsync(first, productId);
            Assert.True(await Eventually.WaitAsync(async () =>
                (await db.InvocationsAsync(InventoryModule.HandlerName, productId)).Count == 1, TimeSpan.FromSeconds(15)));
            await first.StopAsync();
        }

        var pending = Assert.Single(await db.IncomingAsync());
        Assert.Equal(("Incoming", 0, InventoryModule.Subscription), (pending.Status, pending.OwnerId, pending.Subscription));
        HandlerScript.Set(InventoryModule.HandlerName, productId, new Plan(FailureKind.FailedResult, 0));
        return pending;
    }
}
