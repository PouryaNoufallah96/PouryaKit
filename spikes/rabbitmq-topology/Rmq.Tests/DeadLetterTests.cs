using Microsoft.Extensions.Hosting;
using Rmq.Contracts;
using Rmq.Inventory;
using Rmq.Kit;

namespace Rmq.Tests;

/// <summary>The end of the failing subscription's retries: its delivery alone is set aside, in Wolverine's storage.</summary>
public sealed class DeadLetterTests(Servers servers)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(Topology.QueuePerSubscription, "rabbitmq://queue/inventory.audit-product")]
    [InlineData(Topology.ReceiveOnceThenLocalQueues, "local://inventory.audit-product/")]
    public async Task Exhausted_retries_set_aside_only_the_failing_subscriptions_delivery_in_wolverine_storage(Topology topology, string receivedAt)
    {
        await using var environment = await servers.CreateEnvironmentAsync();
        IHost? consumer = null, producer = null;
        try
        {
            consumer = await Hosts.StartConsumerAsync(
                environment,
                new ConsumerSettings(topology, AuditFailures: int.MaxValue, RetryDelays: [TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500)]));
            producer = await Hosts.StartProducerAsync(environment);
            var productId = Guid.CreateVersion7();
            var messageId = await Hosts.PublishAsync(producer, productId);

            Assert.True(await Eventually.WaitAsync(async () => (await environment.DeadLettersAsync()).Count == 1, Timeout));
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

            var deadLetter = Assert.Single(await environment.DeadLettersAsync());
            Assert.Equal(receivedAt, deadLetter.ReceivedAt);
            Assert.Equal(ProductCreatedV1.WireName, deadLetter.MessageType);
            Assert.Contains(nameof(HandlerFailedException), deadLetter.ExceptionType);
            Assert.Equal(["failed", "failed", "failed"], await environment.AttemptsAsync(InventoryModule.AuditProduct, messageId));
            Assert.Equal(0, await environment.EffectsAsync(InventoryModule.AuditProduct, productId));
            Assert.Equal(["succeeded"], await environment.AttemptsAsync(InventoryModule.ReserveStock, messageId));
            Assert.Equal(1, await environment.ReceiptsAsync(InventoryModule.ReserveStock, messageId));
            // No broker dead-letter queue was declared.
            Assert.False(await environment.BrokerQueueExistsAsync("wolverine-dead-letter-queue"));
        }
        finally
        {
            await Hosts.StopAsync(producer, consumer);
        }
    }
}
