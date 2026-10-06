using Microsoft.Extensions.Hosting;
using Rmq.Inventory;
using Rmq.Kit;

namespace Rmq.Tests;

/// <summary>A publication whose only subscribers live in another Host.</summary>
public sealed class RemoteOnlyPublicationTests(Servers servers)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Theory]
    [InlineData(Topology.QueuePerSubscription)]
    [InlineData(Topology.ReceiveOnceThenLocalQueues)]
    public async Task A_publication_with_no_subscriber_in_its_own_host_is_stored_in_the_commit_and_delivered_to_the_other_host(Topology topology)
    {
        await using var environment = await servers.CreateEnvironmentAsync();
        IHost? consumer = null, producer = null;
        try
        {
            consumer = await Hosts.StartConsumerAsync(environment, new ConsumerSettings(topology));
            producer = await Hosts.StartProducerAsync(environment);
            var productId = Guid.CreateVersion7();
            List<(long Rows, long SameCommit)> beforeFlush = [];

            var messageId = await Hosts.PublishAsync(producer, productId, async () =>
                beforeFlush.Add((
                    await environment.ProducerCountAsync("select count(*) from wolverine.wolverine_outgoing_envelopes"),
                    await environment.ProducerCountAsync(
                        """
                        select count(*) from wolverine.wolverine_outgoing_envelopes e, catalog.products p
                        where p.id = $1 and pg_xact_commit_timestamp(e.xmin) = pg_xact_commit_timestamp(p.xmin)
                        """,
                        productId))));

            // One outgoing envelope, to the exchange, written in the business row's commit.
            Assert.Equal([(1L, 1L)], beforeFlush);
            Assert.True(await Eventually.WaitAsync(
                async () => await environment.ReceiptsAsync(InventoryModule.ReserveStock, messageId) == 1
                    && await environment.ReceiptsAsync(InventoryModule.AuditProduct, messageId) == 1,
                Timeout));
            Assert.Equal(1, await environment.EffectsAsync(InventoryModule.ReserveStock, productId));
            Assert.Equal(1, await environment.EffectsAsync(InventoryModule.AuditProduct, productId));
            Assert.True(await Eventually.WaitAsync(
                async () => await environment.ProducerCountAsync("select count(*) from wolverine.wolverine_outgoing_envelopes") == 0,
                Timeout));
        }
        finally
        {
            await Hosts.StopAsync(producer, consumer);
        }
    }

    /// <summary>Control: the Kit today routes only to local subscriptions; a Host with none writes nothing.</summary>
    [Fact]
    public async Task Without_a_route_from_the_publication_to_the_broker_nothing_is_written_and_nothing_arrives()
    {
        await using var environment = await servers.CreateEnvironmentAsync();
        IHost? consumer = null, producer = null;
        try
        {
            consumer = await Hosts.StartConsumerAsync(environment, new ConsumerSettings(Topology.ReceiveOnceThenLocalQueues));
            producer = await Hosts.StartProducerAsync(environment, routeToBroker: false);
            var productId = Guid.CreateVersion7();
            long outgoing = -1;

            var messageId = await Hosts.PublishAsync(producer, productId, async () =>
                outgoing = await environment.ProducerCountAsync("select count(*) from wolverine.wolverine_outgoing_envelopes"));

            Assert.Equal(0, outgoing);
            Assert.Equal(1, await environment.ProducerCountAsync("select count(*) from catalog.products where id = $1", productId));
            Assert.False(await Eventually.WaitAsync(
                async () => await environment.ConsumerCountAsync("select count(*) from inventory.receipts") > 0,
                TimeSpan.FromSeconds(3)));
            Assert.Empty(await environment.AttemptsAsync(InventoryModule.ReserveStock, messageId));
        }
        finally
        {
            await Hosts.StopAsync(producer, consumer);
        }
    }

    /// <summary>
    /// The broker drops a message published to an exchange with no bound queue. Under both topologies the queues are
    /// declared by the subscribing Host, so a publication before that Host ever started is lost.
    /// </summary>
    [Theory]
    [InlineData(Topology.QueuePerSubscription)]
    [InlineData(Topology.ReceiveOnceThenLocalQueues)]
    public async Task A_publication_before_the_subscribing_host_ever_declared_its_queues_is_dropped_by_the_broker(Topology topology)
    {
        await using var environment = await servers.CreateEnvironmentAsync();
        IHost? consumer = null, producer = null;
        try
        {
            producer = await Hosts.StartProducerAsync(environment);
            var productId = Guid.CreateVersion7();
            var messageId = await Hosts.PublishAsync(producer, productId);
            // The outbox deletes its row once the broker confirmed the publish.
            Assert.True(await Eventually.WaitAsync(
                async () => await environment.ProducerCountAsync("select count(*) from wolverine.wolverine_outgoing_envelopes") == 0,
                Timeout));

            consumer = await Hosts.StartConsumerAsync(environment, new ConsumerSettings(topology));
            Assert.False(await Eventually.WaitAsync(
                async () => await environment.ConsumerCountAsync("select count(*) from inventory.receipts") > 0
                    || (await environment.InboxAsync()).Count > 0,
                TimeSpan.FromSeconds(4)));
            Assert.Empty(await environment.AttemptsAsync(InventoryModule.ReserveStock, messageId));
        }
        finally
        {
            await Hosts.StopAsync(producer, consumer);
        }
    }
}
