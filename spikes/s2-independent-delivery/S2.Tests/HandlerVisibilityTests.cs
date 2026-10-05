using S2.Billing;
using S2.Host;
using S2.Inventory;
using S2.Shipping;

namespace S2.Tests;

/// <summary>
/// Plan section D and ADR 0001 keep a Module's handlers internal. Shipping's Integration Event Handler is internal and
/// its subscription name is mixed case (<c>shipping.Product-Created</c>).
/// </summary>
public sealed class HandlerVisibilityTests(PostgresServer server)
{
    /// <summary>
    /// Option A's Wolverine handler type is a closed generic over the Kit handler, and Wolverine compiles the code that
    /// calls it into an in-memory assembly with a random name (JasperFx.RuntimeCompiler 5.0.0,
    /// <c>AssemblyName ?? Path.GetRandomFileName()</c>). The host starts; the first delivery for that subscription
    /// fails to compile and goes to the error queue. The other chains compile on their own and deliver.
    /// </summary>
    [Fact]
    public async Task Separated_queues_cannot_deliver_to_an_internal_handler()
    {
        await using var db = await server.CreateDatabaseAsync();
        var productId = Guid.NewGuid();
        using var host = await db.StartHostAsync(new SpikeSettings(db.ConnectionString, DeliveryChoice.SeparatedQueues, ShippingSubscribes: true));

        await SpikeDatabase.PublishAsync(host, productId);

        Assert.True(await Eventually.WaitAsync(async () => (await db.DeadLettersAsync()).Count == 1, TimeSpan.FromSeconds(15)));
        var deadLetter = Assert.Single(await db.DeadLettersAsync());
        Assert.Equal($"local://{ShippingModule.Subscription.ToLowerInvariant()}/", deadLetter.ReceivedAt);
        Assert.Contains($"'{ShippingModule.HandlerName}' is inaccessible due to its protection level", deadLetter.ExceptionMessage);
        Assert.Empty(await db.InvocationsAsync(ShippingModule.HandlerName, productId));
        Assert.True(await Eventually.WaitAsync(async () =>
            (await db.InvocationsAsync(InventoryModule.HandlerName, productId)).Count == 1 &&
            (await db.InvocationsAsync(BillingModule.HandlerName, productId)).Count == 1, TimeSpan.FromSeconds(10)));
        Assert.Equal(["Succeeded"], await db.InvocationsAsync(BillingModule.HandlerName, productId));
    }

    /// <summary>
    /// Option C runs no generated code; Option B's and the key-type Option A's generated code names only public types
    /// (the Contracts record, the Kit envelope or key type), and all three find the internal handler through the Kit.
    /// Option C's queue is <c>local://shipping.product-created/</c>: it finds the subscription ignoring case.
    /// </summary>
    [Theory]
    [InlineData(DeliveryChoice.RoutedQueues)]
    [InlineData(DeliveryChoice.DeliveryEnvelope)]
    [InlineData(DeliveryChoice.SeparatedSlots)]
    public async Task Delivers_to_an_internal_handler(DeliveryChoice delivery)
    {
        await using var db = await server.CreateDatabaseAsync();
        var productId = Guid.NewGuid();
        using var host = await db.StartHostAsync(new SpikeSettings(db.ConnectionString, delivery, ShippingSubscribes: true));

        await SpikeDatabase.PublishAsync(host, productId);

        Assert.True(await Eventually.WaitAsync(async () =>
            (await db.InvocationsAsync(ShippingModule.HandlerName, productId)).Count == 1, TimeSpan.FromSeconds(15)));
        Assert.Equal(["Succeeded"], await db.InvocationsAsync(ShippingModule.HandlerName, productId));
        Assert.Empty(await db.DeadLettersAsync());
    }
}
