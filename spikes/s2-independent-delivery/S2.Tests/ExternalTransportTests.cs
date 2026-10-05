using S2.Billing;
using S2.Catalog;
using S2.Host;
using S2.Inventory;
using S2.Kit.Harness;

namespace S2.Tests;

/// <summary>
/// Option C with the deliveries arriving from a transport other than local queues: Wolverine's built-in TCP transport,
/// one listener with a durable inbox per subscription, standing in for a broker queue per subscription. The bare
/// Integration Event crosses the wire under its own wire name, and the Kit handler finds the subscription from the
/// listener the envelope arrived at.
/// </summary>
public sealed class ExternalTransportTests(PostgresServer server)
{
    [Fact]
    public async Task Each_handler_is_retried_on_its_own_when_deliveries_arrive_over_tcp()
    {
        await using var db = await server.CreateDatabaseAsync();
        var productId = Guid.NewGuid();
        var firstPort = Random.Shared.Next(20_000, 40_000);
        HandlerScript.Set(InventoryModule.HandlerName, productId, new Plan(FailureKind.FailedResult, Failures: 2));
        using var host = await db.StartHostAsync(new SpikeSettings(db.ConnectionString, DeliveryChoice.RoutedQueuesOverTcp, FirstTcpPort: firstPort));

        await SpikeDatabase.PublishAsync(host, productId);

        Assert.True(await Eventually.WaitAsync(async () =>
            (await db.InvocationsAsync(InventoryModule.HandlerName, productId)).Contains("Succeeded"), TimeSpan.FromSeconds(15)));
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(["FailedResult", "FailedResult", "Succeeded"], await db.InvocationsAsync(InventoryModule.HandlerName, productId));
        Assert.Equal(["Succeeded"], await db.InvocationsAsync(BillingModule.HandlerName, productId));

        // Billing is configured first, so it listens on the first port.
        var rows = await db.IncomingAsync();
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join("; ", rows));
        Assert.Equal([$"tcp://localhost:{firstPort}/", $"tcp://localhost:{firstPort + 1}/"], rows.Select(r => r.ReceivedAt).Order());
        Assert.All(rows, r => Assert.Equal((CatalogModule.ProductCreatedWireName, "Handled"), (r.MessageType, r.Status)));
        Assert.Equal(0, await db.CountAsync("select count(*) from wolverine.wolverine_outgoing_envelopes"));
    }
}
