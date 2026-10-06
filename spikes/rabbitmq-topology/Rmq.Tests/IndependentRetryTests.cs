using Microsoft.Extensions.Hosting;
using Rmq.Inventory;
using Rmq.Kit;

namespace Rmq.Tests;

/// <summary>One subscription fails and is retried while the other, to the same wire name, succeeds.</summary>
public sealed class IndependentRetryTests(Servers servers)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(Topology.QueuePerSubscription)]
    [InlineData(Topology.ReceiveOnceThenLocalQueues)]
    public async Task A_failing_subscription_is_retried_on_its_own_and_never_reruns_or_starves_the_other(Topology topology)
    {
        await using var environment = await servers.CreateEnvironmentAsync();
        IHost? consumer = null, producer = null;
        try
        {
            // Audit fails its first two attempts of each message; stored retries one second apart.
            consumer = await Hosts.StartConsumerAsync(environment, new ConsumerSettings(topology, AuditFailures: 2));
            producer = await Hosts.StartProducerAsync(environment);
            List<(Guid Product, Guid Message)> published = [];
            for (var i = 0; i < 3; i++)
            {
                var productId = Guid.CreateVersion7();
                published.Add((productId, await Hosts.PublishAsync(producer, productId)));
            }

            // Not starved: every succeeding delivery commits while the failing ones are still waiting for a retry.
            Assert.True(await Eventually.WaitAsync(
                async () => await environment.ConsumerCountAsync(
                    "select count(*) from inventory.receipts where subscription = $1", InventoryModule.ReserveStock) == 3,
                Timeout));
            Assert.Equal(0, await environment.ConsumerCountAsync(
                "select count(*) from inventory.receipts where subscription = $1", InventoryModule.AuditProduct));
            Assert.Equal(0, await environment.ConsumerCountAsync(
                "select count(*) from inventory.effects where subscription = $1", InventoryModule.AuditProduct));

            Assert.True(await Eventually.WaitAsync(
                async () => await environment.ConsumerCountAsync(
                    "select count(*) from inventory.receipts where subscription = $1", InventoryModule.AuditProduct) == 3,
                Timeout));
            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            foreach (var (productId, messageId) in published)
            {
                // Not rerun: the succeeding subscription ran once per message, the failing one three times.
                Assert.Equal(["succeeded"], await environment.AttemptsAsync(InventoryModule.ReserveStock, messageId));
                Assert.Equal(["failed", "failed", "succeeded"], await environment.AttemptsAsync(InventoryModule.AuditProduct, messageId));
                foreach (var subscription in new[] { InventoryModule.ReserveStock, InventoryModule.AuditProduct })
                {
                    Assert.Equal(1, await environment.EffectsAsync(subscription, productId));
                    Assert.Equal(1, await environment.ReceiptsAsync(subscription, messageId));
                    // The Receipt and the effect are one commit.
                    Assert.Equal(1, await environment.ConsumerCountAsync(
                        """
                        select count(*) from inventory.receipts r, inventory.effects e
                        where r.subscription = $1 and r.message_id = $2 and e.subscription = $1 and e.product_id = $3
                          and pg_xact_commit_timestamp(r.xmin) = pg_xact_commit_timestamp(e.xmin)
                        """,
                        subscription, messageId, productId));
                }
            }

            Assert.Empty(await environment.DeadLettersAsync());
        }
        finally
        {
            await Hosts.StopAsync(producer, consumer);
        }
    }
}
