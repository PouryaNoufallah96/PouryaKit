using S2.Catalog;
using S2.Host;
using S2.Inventory;
using S2.Kit.Harness;
using Wolverine.Runtime.Handlers;

namespace S2.Tests;

/// <summary>
/// Inventory is the only subscriber; its delivery fails and waits a minute for its retry when the Host stops
/// gracefully. The next deployment adds Billing as a second subscriber, and Inventory would now succeed.
/// </summary>
public sealed class TopologyTests(PostgresServer server)
{
    /// <summary>
    /// Wolverine separates handlers only when a message type has more than one (<c>HandlerChain</c> calls
    /// <c>maybeAssignStickyHandlers</c> only for <c>grouping.Count() &gt; 1</c>), so without pinning, a lone subscriber's
    /// delivery goes to the message type's queue despite <c>AddStickyHandler</c>. Once a second subscriber exists, that
    /// queue has no handler, and the pending delivery is dead-lettered with <see cref="NoHandlerForEndpointException"/>.
    /// </summary>
    [Fact]
    public async Task Unpinned_separated_queues_dead_letter_a_pending_delivery_when_a_second_subscriber_is_added()
    {
        await using var db = await server.CreateDatabaseAsync();
        var productId = Guid.NewGuid();

        var pending = await LeavePendingDeliveryWithOneSubscriberAsync(db, DeliveryChoice.SeparatedQueuesUnpinned, productId);
        Assert.Equal($"local://{CatalogModule.ProductCreatedWireName}/", pending.ReceivedAt);

        using var second = await StartWithTwoSubscribersAsync(db, DeliveryChoice.SeparatedQueuesUnpinned, productId);

        Assert.True(await Eventually.WaitAsync(async () => (await db.DeadLettersAsync()).Count == 1, TimeSpan.FromSeconds(20)));
        var deadLetter = Assert.Single(await db.DeadLettersAsync());
        Assert.Equal((pending.Id, typeof(NoHandlerForEndpointException).FullName), (deadLetter.Id, deadLetter.ExceptionType));
        Assert.Equal(["FailedResult"], await db.InvocationsAsync(InventoryModule.HandlerName, productId));
    }

    /// <summary>
    /// Option C routes every subscription to its own queue whatever their number, with no special case. Option A pins a
    /// lone subscriber's messages to its subscription queue, the queue its sticky handler keeps once Wolverine
    /// separates. Option B's queue depends on the message type only and its envelope names its subscription. In each
    /// case the pending delivery reaches Inventory.
    /// </summary>
    [Theory]
    [InlineData(DeliveryChoice.RoutedQueues)]
    [InlineData(DeliveryChoice.SeparatedQueues)]
    [InlineData(DeliveryChoice.DeliveryEnvelope)]
    public async Task A_pending_delivery_is_delivered_after_a_second_subscriber_is_added(DeliveryChoice delivery)
    {
        await using var db = await server.CreateDatabaseAsync();
        var productId = Guid.NewGuid();

        var pending = await LeavePendingDeliveryWithOneSubscriberAsync(db, delivery, productId);
        Assert.Equal(InventoryModule.Subscription, pending.Subscription);

        using var second = await StartWithTwoSubscribersAsync(db, delivery, productId);

        Assert.True(await Eventually.WaitAsync(async () =>
            (await db.InvocationsAsync(InventoryModule.HandlerName, productId)).Contains("Succeeded"), TimeSpan.FromSeconds(20)));
        Assert.Equal(["FailedResult", "Succeeded"], await db.InvocationsAsync(InventoryModule.HandlerName, productId));
        Assert.Empty(await db.DeadLettersAsync());
    }

    private static readonly TimeSpan[] LongRetry = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)];

    private static async Task<EnvelopeRow> LeavePendingDeliveryWithOneSubscriberAsync(SpikeDatabase db, DeliveryChoice delivery, Guid productId)
    {
        HandlerScript.Set(InventoryModule.HandlerName, productId, Plan.FailForever());
        using (var first = await db.StartHostAsync(new SpikeSettings(db.ConnectionString, delivery, BillingSubscribes: false, RetryCooldowns: LongRetry)))
        {
            await SpikeDatabase.PublishAsync(first, productId);
            Assert.True(await Eventually.WaitAsync(async () =>
                (await db.InvocationsAsync(InventoryModule.HandlerName, productId)).Count == 1, TimeSpan.FromSeconds(15)));
            await first.StopAsync();
        }

        var pending = Assert.Single(await db.IncomingAsync());
        Assert.Equal(("Incoming", 0), (pending.Status, pending.OwnerId));
        return pending;
    }

    private static Task<Microsoft.Extensions.Hosting.IHost> StartWithTwoSubscribersAsync(SpikeDatabase db, DeliveryChoice delivery, Guid productId)
    {
        HandlerScript.Set(InventoryModule.HandlerName, productId, new Plan(FailureKind.FailedResult, 0));
        return db.StartHostAsync(new SpikeSettings(db.ConnectionString, delivery, RetryCooldowns: LongRetry));
    }
}
