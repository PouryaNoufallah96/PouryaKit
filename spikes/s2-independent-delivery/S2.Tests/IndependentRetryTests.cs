using S2.Billing;
using S2.Host;
using S2.Inventory;
using S2.Kit.Harness;

namespace S2.Tests;

public sealed class IndependentRetryTests(PostgresServer server)
{
    public static TheoryData<DeliveryChoice, FailureKind> Options => new()
    {
        { DeliveryChoice.RoutedQueues, FailureKind.FailedResult },
        { DeliveryChoice.RoutedQueues, FailureKind.Throw },
        { DeliveryChoice.SeparatedQueues, FailureKind.FailedResult },
        { DeliveryChoice.SeparatedQueues, FailureKind.Throw },
        { DeliveryChoice.DeliveryEnvelope, FailureKind.FailedResult },
        { DeliveryChoice.DeliveryEnvelope, FailureKind.Throw },
    };

    [Theory]
    [MemberData(nameof(Options))]
    public async Task Each_handler_is_retried_on_its_own(DeliveryChoice delivery, FailureKind kind)
    {
        await using var db = await server.CreateDatabaseAsync();
        var productId = Guid.NewGuid();
        HandlerScript.Set(InventoryModule.HandlerName, productId, new Plan(kind, Failures: 2));
        using var host = await db.StartHostAsync(delivery);

        await SpikeDatabase.PublishAsync(host, productId);

        Assert.True(await Eventually.WaitAsync(async () =>
            (await db.InvocationsAsync(InventoryModule.HandlerName, productId)).Contains("Succeeded"), TimeSpan.FromSeconds(15)));
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal([kind.ToString(), kind.ToString(), "Succeeded"], await db.InvocationsAsync(InventoryModule.HandlerName, productId));
        Assert.Equal(["Succeeded"], await db.InvocationsAsync(BillingModule.HandlerName, productId));
    }

    /// <summary>
    /// Plan section F leaves "per-retry scope" open. Each attempt goes through <c>Subscription.DeliverAsync</c>, which
    /// opens a new scope; the handler's scoped <c>DeliveryLog</c> records which scope it ran in.
    /// </summary>
    [Theory]
    [InlineData(DeliveryChoice.RoutedQueues)]
    [InlineData(DeliveryChoice.SeparatedQueues)]
    [InlineData(DeliveryChoice.DeliveryEnvelope)]
    public async Task Each_attempt_runs_in_a_new_scope(DeliveryChoice delivery)
    {
        await using var db = await server.CreateDatabaseAsync();
        var productId = Guid.NewGuid();
        HandlerScript.Set(InventoryModule.HandlerName, productId, new Plan(FailureKind.FailedResult, Failures: 2));
        using var host = await db.StartHostAsync(delivery);

        await SpikeDatabase.PublishAsync(host, productId);

        Assert.True(await Eventually.WaitAsync(async () =>
            (await db.InvocationsAsync(InventoryModule.HandlerName, productId)).Contains("Succeeded"), TimeSpan.FromSeconds(15)));
        var scopes = await db.ScopesAsync(InventoryModule.HandlerName, productId);
        Assert.Equal(3, scopes.Count);
        Assert.Equal(3, scopes.Distinct().Count());
    }

    /// <summary>
    /// Control for the first test: the same adapters and the same script under Wolverine's default
    /// <c>ClassicCombineIntoOneLogicalHandler</c>. Both handlers become one logical handler on one envelope, so
    /// Inventory's failures re-run Billing. This shows the test above can tell independent retry from shared retry.
    /// </summary>
    [Fact]
    public async Task Combined_handler_reruns_the_handler_that_already_succeeded()
    {
        await using var db = await server.CreateDatabaseAsync();
        var productId = Guid.NewGuid();
        HandlerScript.Set(InventoryModule.HandlerName, productId, new Plan(FailureKind.FailedResult, Failures: 2));
        using var host = await db.StartHostAsync(DeliveryChoice.ClassicCombined);

        await SpikeDatabase.PublishAsync(host, productId);

        Assert.True(await Eventually.WaitAsync(async () =>
            (await db.InvocationsAsync(InventoryModule.HandlerName, productId)).Contains("Succeeded"), TimeSpan.FromSeconds(15)));
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(["Succeeded", "Succeeded", "Succeeded"], await db.InvocationsAsync(BillingModule.HandlerName, productId));
        Assert.Single(await db.IncomingAsync());
    }
}
