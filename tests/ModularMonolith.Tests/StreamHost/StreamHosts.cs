using Catalog.Contracts.Products.Events.V1;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModularMonolith.Tests.Streams;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Messaging.Wolverine.Marten;
using PouryaKit.Modularity;
using PouryaKit.Testing;

namespace ModularMonolith.Tests.StreamHost;

/// <summary>The Hosts of Stream Modules alone that the Stream tests compose.</summary>
public static class StreamHosts
{
    /// <summary>
    /// A Host whose one Module is the Stream Module Accounts, over the database of <paramref name="connectionString"/>,
    /// composed and not built: its message store is the Marten registration's, and it delivers at test speed. With
    /// <paramref name="martenFirst"/>, the Marten store is registered before the Module.
    /// </summary>
    public static HostApplicationBuilder MartenOnly(string connectionString, bool martenFirst = false)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            ["--environment", Environments.Development, $"--ConnectionStrings:app={connectionString}"]
        );
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        if (martenFirst)
        {
            builder.UsePouryaKitMarten(connectionName: "app");
        }

        builder.AddPouryaKitModules(modules => modules.Add<AccountsModule>());
        // Catalog, whose Snapshots Accounts subscribes to, is another Host's here.
        builder.Services.ReceiveSnapshot<ProductSnapshotV1>("catalog.product-snapshot.v1");
        if (!martenFirst)
        {
            builder.UsePouryaKitMarten(connectionName: "app");
        }

        builder.Services.UsePouryaKitTestDelivery();
        return builder;
    }
}
