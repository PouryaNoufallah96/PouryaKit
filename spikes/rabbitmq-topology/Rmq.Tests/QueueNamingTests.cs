using Microsoft.Extensions.Hosting;
using Rmq.Inventory;
using Rmq.Kit;

namespace Rmq.Tests;

/// <summary>
/// Two services on one broker, each with its own database, that declare the same subscription names. The Kit checks
/// subscription names per Host only.
/// </summary>
public sealed class QueueNamingTests(Servers servers)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(Topology.QueuePerSubscription)]
    [InlineData(Topology.ReceiveOnceThenLocalQueues)]
    public async Task Two_services_that_declare_the_same_subscription_names_each_receive_every_message_only_when_the_broker_queue_is_the_hosts(Topology topology)
    {
        await using var environment = await servers.CreateEnvironmentAsync();
        await using var other = await servers.AddConsumerAsync(environment);
        IHost? inventory = null, warehouse = null, producer = null;
        try
        {
            inventory = await Hosts.StartConsumerAsync(environment, new ConsumerSettings(topology));
            warehouse = await Hosts.StartConsumerAsync(other, new ConsumerSettings(topology, ServiceName: "warehouse"));
            producer = await Hosts.StartProducerAsync(environment);
            const int published = 6;
            for (var i = 0; i < published; i++)
            {
                await Hosts.PublishAsync(producer, Guid.CreateVersion7());
            }

            const string receipts = "select count(*) from inventory.receipts where subscription = $1";
            Assert.True(await Eventually.WaitAsync(
                async () => await environment.ConsumerCountAsync(receipts, InventoryModule.ReserveStock)
                    + await other.ConsumerCountAsync(receipts, InventoryModule.ReserveStock) >= published,
                Timeout));
            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            var atInventory = await environment.ConsumerCountAsync(receipts, InventoryModule.ReserveStock);
            var atWarehouse = await other.ConsumerCountAsync(receipts, InventoryModule.ReserveStock);
            if (topology == Topology.QueuePerSubscription)
            {
                // One queue named by the subscription, two competing consumers: the messages are split between them.
                Assert.Equal(published, atInventory + atWarehouse);
                Assert.True(atInventory < published && atWarehouse < published, $"inventory {atInventory}, warehouse {atWarehouse}");
            }
            else
            {
                // A queue per Host: each service receives every message.
                Assert.Equal(published, atInventory);
                Assert.Equal(published, atWarehouse);
            }
        }
        finally
        {
            await Hosts.StopAsync(producer, inventory, warehouse);
        }
    }
}
