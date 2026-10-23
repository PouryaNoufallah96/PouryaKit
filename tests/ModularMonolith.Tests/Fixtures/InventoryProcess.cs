using Inventory.Host;

namespace ModularMonolith.Tests.Fixtures;

// Inventory.Host as its own process, as HostProcess runs one, on the test's Inventory database and broker vhost.
internal static class InventoryProcess
{
    public static HostProcess Start(BrokerEnvironment environment) =>
        HostProcess.Start(
            "Inventory.Host",
            new Dictionary<string, string>
            {
                ["ConnectionStrings__app"] = environment.Inventory,
                [$"ConnectionStrings__{InventoryHost.BrokerConnection}"] = environment.Broker.ToString(),
            }
        );
}
