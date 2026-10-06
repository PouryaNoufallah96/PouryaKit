using Microsoft.Extensions.Hosting;
using Rmq.Contracts;
using Rmq.Inventory;
using Rmq.Kit;
using Wolverine;

namespace Rmq.Tests;

/// <summary>Which envelope ids each subscription's delivery has, and what the Inbox keeps of them.</summary>
public sealed class FanOutIdentityTests(Servers servers)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Topology A under Wolverine's default identity: the broker copies one envelope, id included, to both queues. The
    /// Inbox's key is the id alone, so the second copy is taken for a duplicate and acknowledged without running.
    /// </summary>
    [Fact]
    public async Task Queue_per_subscription_with_IdOnly_keeps_one_copy_and_acknowledges_the_other_as_a_duplicate()
    {
        await using var environment = await servers.CreateEnvironmentAsync();
        IHost? consumer = null, producer = null;
        try
        {
            consumer = await Hosts.StartConsumerAsync(
                environment,
                new ConsumerSettings(Topology.QueuePerSubscription, MessageIdentity.IdOnly));
            producer = await Hosts.StartProducerAsync(environment);
            var productId = Guid.CreateVersion7();
            var messageId = await Hosts.PublishAsync(producer, productId);

            Assert.True(await Eventually.WaitAsync(
                async () => await environment.ConsumerCountAsync("select count(*) from inventory.receipts") >= 1,
                Timeout));
            // Both queues are drained, yet only one subscription ever runs.
            Assert.True(await Eventually.WaitAsync(
                async () => await environment.BrokerMessagesAsync(InventoryModule.ReserveStock) == 0
                    && await environment.BrokerMessagesAsync(InventoryModule.AuditProduct) == 0,
                Timeout));
            await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            var receipts = await environment.ReceiptsAsync(InventoryModule.ReserveStock, messageId)
                + await environment.ReceiptsAsync(InventoryModule.AuditProduct, messageId);
            Assert.Equal(1, receipts);
            var inbox = await environment.InboxAsync();
            Assert.Single(inbox);
            Assert.Equal(ProductCreatedV1.WireName, inbox[0].MessageType);
            Assert.Equal(1, await environment.ConsumerCountAsync("select count(*) from spike.attempts"));
        }
        finally
        {
            await Hosts.StopAsync(producer, consumer);
        }
    }

    /// <summary>Topology A under <c>IdAndDestination</c>: one envelope id, one Inbox row per subscription's queue.</summary>
    [Fact]
    public async Task Queue_per_subscription_with_IdAndDestination_keeps_one_envelope_id_once_per_subscription_queue()
    {
        await using var environment = await servers.CreateEnvironmentAsync();
        IHost? consumer = null, producer = null;
        try
        {
            consumer = await Hosts.StartConsumerAsync(
                environment,
                new ConsumerSettings(Topology.QueuePerSubscription, MessageIdentity.IdAndDestination));
            producer = await Hosts.StartProducerAsync(environment);
            var productId = Guid.CreateVersion7();
            var messageId = await Hosts.PublishAsync(producer, productId);

            Assert.True(await Eventually.WaitAsync(
                async () => await environment.ReceiptsAsync(InventoryModule.ReserveStock, messageId) == 1
                    && await environment.ReceiptsAsync(InventoryModule.AuditProduct, messageId) == 1,
                Timeout));

            var inbox = await environment.InboxAsync();
            Assert.Equal(2, inbox.Count);
            Assert.Single(inbox.Select(row => row.Id).Distinct());
            Assert.Equal(
                ["rabbitmq://queue/inventory.audit-product", "rabbitmq://queue/inventory.reserve-stock"],
                inbox.Select(row => row.ReceivedAt).Order());
            Assert.All(inbox, row => Assert.Equal(ProductCreatedV1.WireName, row.MessageType));
        }
        finally
        {
            await Hosts.StopAsync(producer, consumer);
        }
    }

    /// <summary>
    /// Topology B: the broker envelope is stored once, at the Host's queue; each local copy gets an id of its own. The
    /// Receipt's identity is the Domain Event id header, which survives the broker hop and the fan-out.
    /// </summary>
    [Fact]
    public async Task Receive_once_stores_the_broker_envelope_once_and_gives_each_local_copy_an_id_of_its_own()
    {
        await using var environment = await servers.CreateEnvironmentAsync();
        IHost? consumer = null, producer = null;
        try
        {
            consumer = await Hosts.StartConsumerAsync(environment, new ConsumerSettings(Topology.ReceiveOnceThenLocalQueues));
            producer = await Hosts.StartProducerAsync(environment);
            var productId = Guid.CreateVersion7();
            var messageId = await Hosts.PublishAsync(producer, productId);

            Assert.True(await Eventually.WaitAsync(
                async () => await environment.ReceiptsAsync(InventoryModule.ReserveStock, messageId) == 1
                    && await environment.ReceiptsAsync(InventoryModule.AuditProduct, messageId) == 1,
                Timeout));

            var inbox = await environment.InboxAsync();
            Assert.Equal(
                ["local://inventory.audit-product/", "local://inventory.reserve-stock/", "rabbitmq://queue/inventory"],
                inbox.Select(row => row.ReceivedAt).Order());
            Assert.Equal(3, inbox.Select(row => row.Id).Distinct().Count());
            Assert.All(inbox, row => Assert.Equal(ProductCreatedV1.WireName, row.MessageType));
        }
        finally
        {
            await Hosts.StopAsync(producer, consumer);
        }
    }
}
