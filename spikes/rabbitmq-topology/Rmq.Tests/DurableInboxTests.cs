using Microsoft.Extensions.Hosting;
using Rmq.Inventory;
using Rmq.Kit;

namespace Rmq.Tests;

/// <summary>What the consumer's durable Inbox keeps across a kill and a restart.</summary>
public sealed class DurableInboxTests(Servers servers)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(Topology.QueuePerSubscription)]
    [InlineData(Topology.ReceiveOnceThenLocalQueues)]
    public async Task A_delivery_waiting_for_its_retry_is_kept_by_the_inbox_across_a_consumer_kill_and_restart(Topology topology)
    {
        await using var environment = await servers.CreateEnvironmentAsync();
        IHost? consumer = null, restarted = null, producer = null;
        string[] brokerQueues = topology == Topology.QueuePerSubscription
            ? [InventoryModule.ReserveStock, InventoryModule.AuditProduct]
            : ["inventory"];
        var auditEndpoint = topology == Topology.QueuePerSubscription
            ? "rabbitmq://queue/inventory.audit-product"
            : "local://inventory.audit-product/";
        try
        {
            // Audit fails once and waits four seconds for its stored retry.
            var settings = new ConsumerSettings(topology, AuditFailures: 1, RetryDelays: [TimeSpan.FromSeconds(4)]);
            consumer = await Hosts.StartConsumerAsync(environment, settings);
            producer = await Hosts.StartProducerAsync(environment);
            var first = Guid.CreateVersion7();
            var firstMessage = await Hosts.PublishAsync(producer, first);

            Assert.True(await Eventually.WaitAsync(
                async () => await environment.ReceiptsAsync(InventoryModule.ReserveStock, firstMessage) == 1
                    && (await environment.AttemptsAsync(InventoryModule.AuditProduct, firstMessage)).Count == 1,
                Timeout));
            Assert.True(await Eventually.WaitAsync(
                async () => (await environment.InboxAsync()).Any(row => row.ReceivedAt == auditEndpoint && row.Status == "Scheduled"),
                Timeout));
            // The broker acknowledged everything: the waiting delivery lives only in the consumer's Inbox.
            foreach (var queue in brokerQueues)
            {
                Assert.Equal(0u, await environment.BrokerMessagesAsync(queue));
            }

            await Hosts.KillAsync(consumer);
            consumer = null;

            // Published while the consumer is down: it waits in the broker.
            var second = Guid.CreateVersion7();
            var secondMessage = await Hosts.PublishAsync(producer, second);
            Assert.True(await Eventually.WaitAsync(
                async () => (await Task.WhenAll(brokerQueues.Select(environment.BrokerMessagesAsync))).All(count => count == 1),
                Timeout));

            restarted = await Hosts.StartConsumerAsync(environment, settings);
            Assert.True(await Eventually.WaitAsync(
                async () => await environment.ReceiptsAsync(InventoryModule.AuditProduct, firstMessage) == 1
                    && await environment.ReceiptsAsync(InventoryModule.AuditProduct, secondMessage) == 1
                    && await environment.ReceiptsAsync(InventoryModule.ReserveStock, secondMessage) == 1,
                Timeout));
            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            Assert.Equal(["succeeded"], await environment.AttemptsAsync(InventoryModule.ReserveStock, firstMessage));
            Assert.Equal(["failed", "succeeded"], await environment.AttemptsAsync(InventoryModule.AuditProduct, firstMessage));
            Assert.Equal(["succeeded"], await environment.AttemptsAsync(InventoryModule.ReserveStock, secondMessage));
            foreach (var product in new[] { first, second })
            {
                Assert.Equal(1, await environment.EffectsAsync(InventoryModule.ReserveStock, product));
                Assert.Equal(1, await environment.EffectsAsync(InventoryModule.AuditProduct, product));
            }
        }
        finally
        {
            await Hosts.StopAsync(producer, consumer, restarted);
        }
    }
}
