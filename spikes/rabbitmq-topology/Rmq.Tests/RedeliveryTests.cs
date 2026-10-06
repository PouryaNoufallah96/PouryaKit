using Microsoft.Extensions.Hosting;
using Rmq.Inventory;
using Rmq.Kit;

namespace Rmq.Tests;

/// <summary>A delivery handed out again after it was applied changes nothing: the Receipt keeps each effect once.</summary>
public sealed class RedeliveryTests(Servers servers)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Every broker envelope in the Inbox is handed out again, as recovery does for a row its owner never finished.</summary>
    [Theory]
    [InlineData(Topology.QueuePerSubscription)]
    [InlineData(Topology.ReceiveOnceThenLocalQueues)]
    public async Task A_broker_envelope_handed_out_again_after_it_was_handled_applies_each_effect_once(Topology topology)
    {
        await using var environment = await servers.CreateEnvironmentAsync();
        IHost? consumer = null, producer = null;
        try
        {
            consumer = await Hosts.StartConsumerAsync(environment, new ConsumerSettings(topology));
            producer = await Hosts.StartProducerAsync(environment);
            var productId = Guid.CreateVersion7();
            var messageId = await Hosts.PublishAsync(producer, productId);
            Assert.True(await Eventually.WaitAsync(
                async () => (await environment.InboxAsync()).All(row => row.Status == "Handled")
                    && await environment.ConsumerCountAsync("select count(*) from inventory.receipts") == 2,
                Timeout));

            var handedOut = await environment.ConsumerExecuteAsync(
                "update wolverine.wolverine_incoming_envelopes set status = 'Incoming', owner_id = 0 where received_at like 'rabbitmq://%'");
            Assert.Equal(topology == Topology.QueuePerSubscription ? 2 : 1, handedOut);

            Assert.True(await Eventually.WaitAsync(
                async () => (await environment.AttemptsAsync(InventoryModule.ReserveStock, messageId)).Count == 2
                    && (await environment.AttemptsAsync(InventoryModule.AuditProduct, messageId)).Count == 2,
                Timeout));
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

            foreach (var subscription in new[] { InventoryModule.ReserveStock, InventoryModule.AuditProduct })
            {
                Assert.Equal(1, await environment.EffectsAsync(subscription, productId));
                Assert.Equal(1, await environment.ReceiptsAsync(subscription, messageId));
                Assert.Equal(["succeeded", "duplicate"], await environment.AttemptsAsync(subscription, messageId));
            }

            // Under B the fan-out ran again: two more local copies, each with a new id.
            var local = (await environment.InboxAsync()).Count(row => row.ReceivedAt.StartsWith("local://", StringComparison.Ordinal));
            Assert.Equal(topology == Topology.QueuePerSubscription ? 0 : 4, local);
        }
        finally
        {
            await Hosts.StopAsync(producer, consumer);
        }
    }

    /// <summary>
    /// Topology B's fan-out is not one commit with marking the broker envelope handled. A consumer killed in between
    /// fans out again after its restart; the Receipts keep each effect once.
    /// </summary>
    [Fact]
    public async Task Receive_once_killed_between_the_fan_out_and_marking_the_broker_envelope_handled_fans_out_again_and_applies_each_effect_once()
    {
        await using var environment = await servers.CreateEnvironmentAsync();
        IHost? consumer = null, restarted = null, producer = null;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource();
        var hooks = new SpikeHooks
        {
            AfterFanOut = _ =>
            {
                reached.TrySetResult();
                return never.Task;
            },
        };
        try
        {
            var settings = new ConsumerSettings(Topology.ReceiveOnceThenLocalQueues);
            consumer = await Hosts.StartConsumerAsync(environment, settings, hooks);
            producer = await Hosts.StartProducerAsync(environment);
            var productId = Guid.CreateVersion7();
            var messageId = await Hosts.PublishAsync(producer, productId);

            await reached.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.True(await Eventually.WaitAsync(
                async () => await environment.ConsumerCountAsync("select count(*) from inventory.receipts") == 2,
                Timeout));
            var broker = Assert.Single(await environment.InboxAsync(), row => row.ReceivedAt.StartsWith("rabbitmq://", StringComparison.Ordinal));
            Assert.Equal("Incoming", broker.Status);
            Assert.NotEqual(0, broker.OwnerId);

            // The held fan-out is abandoned, never released, as a killed process leaves it.
            await Hosts.KillAsync(consumer);
            consumer = null;
            restarted = await Hosts.StartConsumerAsync(environment, settings);

            Assert.True(await Eventually.WaitAsync(
                async () => (await environment.AttemptsAsync(InventoryModule.ReserveStock, messageId)).Count == 2
                    && (await environment.AttemptsAsync(InventoryModule.AuditProduct, messageId)).Count == 2,
                Timeout));
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

            foreach (var subscription in new[] { InventoryModule.ReserveStock, InventoryModule.AuditProduct })
            {
                Assert.Equal(1, await environment.EffectsAsync(subscription, productId));
                Assert.Equal(1, await environment.ReceiptsAsync(subscription, messageId));
                Assert.Equal(["succeeded", "duplicate"], await environment.AttemptsAsync(subscription, messageId));
            }

            var inbox = await environment.InboxAsync();
            Assert.Equal(4, inbox.Count(row => row.ReceivedAt.StartsWith("local://", StringComparison.Ordinal)));
            Assert.Equal("Handled", Assert.Single(inbox, row => row.ReceivedAt.StartsWith("rabbitmq://", StringComparison.Ordinal)).Status);
        }
        finally
        {
            await Hosts.StopAsync(producer, consumer, restarted);
        }
    }
}
