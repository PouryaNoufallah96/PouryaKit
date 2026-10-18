using Microsoft.Extensions.Hosting;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Modularity;

namespace ModularMonolith.Tests.Sagas.Host;

/// <summary>The Host of Orders, Billing and Shipping on one message store, whose Sagas the Saga tests prove.</summary>
public static class SagaHost
{
    /// <summary>
    /// The Host composed over the configuration <paramref name="args"/> give, its message store in the schema
    /// <paramref name="storeSchema"/>, and not built.
    /// </summary>
    public static HostApplicationBuilder Compose(string[] args, string storeSchema = "wolverine")
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        builder.AddPouryaKitModules(modules =>
        {
            modules.Add<OrdersModule>();
            modules.Add<BillingModule>();
            modules.Add<ShippingModule>();
        });
        builder.UsePouryaKitWolverine(connectionName: "app", schema: storeSchema);
        return builder;
    }
}
