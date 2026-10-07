using Catalog.Contracts.Products.Events.V1;
using Catalog.Contracts.Products.Events.V2;
using Inventory;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Messaging.Wolverine.RabbitMq;
using PouryaKit.Modularity;
using PouryaKit.Observability;

namespace Inventory.Host;

/// <summary>
/// The Inventory Module in a Host of its own, with its own database, receiving what Catalog publishes in
/// <c>ModularMonolith.Host</c> over RabbitMQ: the Proving Sample's broker proof Host, not a template. It serves HTTP only
/// for its health endpoints. The composition is shared by <c>Program</c> and the tests that host it.
/// </summary>
public static class InventoryHost
{
    /// <summary>The Host's name on the broker: its queue, unique to this kind of Host.</summary>
    public const string ServiceName = "inventory";

    /// <summary>The connection string of the RabbitMQ broker.</summary>
    public const string BrokerConnection = "broker";

    /// <summary>
    /// The Host's composition, from the configuration <paramref name="args"/> adds to: the connection strings <c>app</c>
    /// (Inventory's database and message store) and <c>broker</c>, and <c>Inventory:ReconcileStock:Every</c>. A test that
    /// composes a second kind of Host on the same broker gives it another <paramref name="serviceName"/>.
    /// </summary>
    public static WebApplicationBuilder CreateBuilder(string[] args, string serviceName = ServiceName)
    {
        var builder = WebApplication.CreateBuilder(args);
        // OpenTelemetry with the Kit's source and meter, health checks, service discovery and the standard HTTP
        // resilience: the recipes of ModularMonolith.ServiceDefaults.
        builder.AddServiceDefaults();
        builder.AddPouryaKitModules(modules => modules.Add<InventoryModule>());
        // Catalog runs in another Host: its wire names, as its Composition Root publishes them.
        builder.Services.Receive<ProductCreatedV1>("catalog.product-created.v1");
        builder.Services.Receive<ProductCreatedV2>("catalog.product-created.v2");
        builder.Services.ReceiveSnapshot<ProductSnapshotV1>("catalog.product-snapshot.v1");
        // An activity and a duration for every delivery, in the trace of the Operation that published it in Catalog.
        builder.Services.AddPouryaKitObservability();
        builder.UsePouryaKitWolverine(connectionName: "app");
        builder.UsePouryaKitRabbitMq(BrokerConnection, serviceName);
        return builder;
    }

    /// <summary>Builds the Host <paramref name="builder"/> composed, with its health endpoints.</summary>
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        var app = builder.Build();
        app.MapDefaultEndpoints();
        return app;
    }
}
