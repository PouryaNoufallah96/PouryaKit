using S2.Billing;
using S2.Host;
using S2.Inventory;
using S2.Shipping;

namespace S2.Tests;

/// <summary>Plan section D and ADR 0001 keep a Module's handlers internal. Shipping's handler is internal.</summary>
public sealed class HandlerVisibilityTests(PostgresServer server)
{
    /// <summary>
    /// Option A's Wolverine handler type is a closed generic over the Kit handler, and Wolverine compiles the code
    /// that calls it into an in-memory assembly with a random name (JasperFx.RuntimeCompiler 5.0.0,
    /// <c>AssemblyName ?? Path.GetRandomFileName()</c>), so no <c>InternalsVisibleTo</c> can reach it. The host starts;
    /// the first message for that subscription fails to compile and goes to the error queue. The other chains compile
    /// on their own and deliver.
    /// </summary>
    [Fact]
    public async Task Separated_queues_cannot_deliver_to_an_internal_handler()
    {
        await using var db = await server.CreateDatabaseAsync();
        var productId = Guid.NewGuid();
        using var host = await db.StartHostAsync(new SpikeSettings(db.ConnectionString, DeliveryChoice.SeparatedQueues, ShippingSubscribes: true));

        await SpikeDatabase.PublishAsync(host, productId);

        Assert.True(await Eventually.WaitAsync(async () =>
            (await db.DeadLettersAsync()).Any(d => d.ReceivedAt == $"local://{ShippingModule.Subscription}/"), TimeSpan.FromSeconds(15)));
        var deadLetter = Assert.Single(await db.DeadLettersAsync());
        Assert.Contains("CS0122", deadLetter.ExceptionMessage);
        Assert.Contains($"'{ShippingModule.HandlerName}' is inaccessible due to its protection level", deadLetter.ExceptionMessage);
        Assert.Empty(await db.InvocationsAsync(ShippingModule.HandlerName, productId));
        Assert.True(await Eventually.WaitAsync(async () =>
            (await db.InvocationsAsync(InventoryModule.HandlerName, productId)).Count == 1, TimeSpan.FromSeconds(10)));
        Assert.Equal(["Succeeded"], await db.InvocationsAsync(BillingModule.HandlerName, productId));
    }

    /// <summary>Option B's Wolverine handler is closed over the public Contracts type only; the Kit resolves the internal handler from DI.</summary>
    [Fact]
    public async Task Delivery_envelope_delivers_to_an_internal_handler()
    {
        await using var db = await server.CreateDatabaseAsync();
        var productId = Guid.NewGuid();
        using var host = await db.StartHostAsync(new SpikeSettings(db.ConnectionString, DeliveryChoice.DeliveryEnvelope, ShippingSubscribes: true));

        await SpikeDatabase.PublishAsync(host, productId);

        Assert.True(await Eventually.WaitAsync(async () =>
            (await db.InvocationsAsync(ShippingModule.HandlerName, productId)).Count == 1, TimeSpan.FromSeconds(15)));
        Assert.Equal(["Succeeded"], await db.InvocationsAsync(ShippingModule.HandlerName, productId));
        Assert.Empty(await db.DeadLettersAsync());
    }
}
