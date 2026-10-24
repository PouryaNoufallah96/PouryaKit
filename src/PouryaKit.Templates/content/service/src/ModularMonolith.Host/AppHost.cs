using Catalog.Contracts.Products.Events.V1;
using Catalog.Contracts.Products.Events.V2;
using Inventory;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Messaging.Wolverine.RabbitMq;
using PouryaKit.Modularity;
using PouryaKit.Observability;
using PouryaKit.Security.JwtBearer;
using PouryaKit.Web;
using PouryaKit.Web.OpenApi;

namespace ModularMonolith.Host;

/// <summary>The Host's composition, shared by <c>Program</c> and the tests that host the application.</summary>
public static class AppHost
{
    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        // OpenTelemetry that exports the Kit's activities and metrics, liveness and readiness checks, service discovery and one
        // standard resilience pipeline for every HttpClient; then Serilog in place of the logging providers. Each recipe is
        // optional and lives in the application's own ServiceDefaults project.
        builder.AddServiceDefaults();
        builder.AddSerilogDefaults();
        // The Host authenticates its callers with bearer tokens. The issuer, audiences and signing keys come from the
        // configuration section Authentication:Schemes:Bearer (`dotnet user-jwts` fills it for development); without them the
        // Host does not start. A token's "sub" is the Actor and its "tenant" the tenant, whose stock items a caller reads.
        builder.Services.AddPouryaKitJwtBearer(claims => claims.TenantClaim = "tenant");
        builder.Services.AddAuthorization();
        // Authenticated by default: AddPouryaKitWeb sets the authorization fallback policy; a public endpoint says so.
        builder.Services.AddPouryaKitWeb();
        builder.Services.AddPouryaKitOpenApi();
        // The one Module through its Composition Root, in the Host's container, which is validated when it is built; the
        // Kit's composition checks stop the Host before it serves.
        builder.AddPouryaKitModules(modules => modules.Add<InventoryModule>());
        // Catalog runs in another Host: what Inventory subscribes to arrives under the wire names Catalog publishes it with.
        builder.Services.Receive<ProductCreatedV1>("catalog.product-created.v1");
        builder.Services.Receive<ProductCreatedV2>("catalog.product-created.v2");
        builder.Services.ReceiveSnapshot<ProductSnapshotV1>("catalog.product-snapshot.v1");
        // An activity and a duration for every command and query, around the dispatcher the Module registered; a delivery's
        // Operation continues the trace of the Operation that published it.
        builder.Services.AddPouryaKitObservability();
        builder.UsePouryaKitWolverine(connectionName: "app");
        // The Host's queue on the broker of ConnectionStrings:broker, bound to the exchange of each wire name it receives.
        builder.UsePouryaKitRabbitMq(connectionName: "broker", serviceName: "modular-monolith");
        return builder;
    }

    /// <summary>Builds the application <paramref name="builder"/> composed, with its request pipeline.</summary>
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        var app = builder.Build();
        // First, so an exception in routing or authentication still answers a problem response.
        app.UseExceptionHandler();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPouryaKitModuleEndpoints();
        // /alive in every environment, for an orchestrator's liveness probe: anonymous, and asking nothing outside the process.
        // /health, which asks the database, and the OpenAPI documents with their Scalar reference are for developers: mapped
        // in Development only, and public there.
        app.MapDefaultEndpoints();
        if (app.Environment.IsDevelopment())
        {
            app.MapPouryaKitOpenApi().AllowAnonymous();
        }

        return app;
    }
}
