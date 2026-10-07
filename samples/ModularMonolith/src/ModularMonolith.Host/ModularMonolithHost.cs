using Billing;
using Catalog;
using Catalog.Products.Events.Notifications;
using Inventory;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Modularity;
using PouryaKit.Observability;
using PouryaKit.Security;
using PouryaKit.Web;
using PouryaKit.Web.OpenApi;

namespace ModularMonolith.Host;

/// <summary>The Host's composition, shared by <c>Program</c> and the tests that host the application.</summary>
public static class ModularMonolithHost
{
    /// <summary>The named HTTP client for calls to another Host.</summary>
    public const string DownstreamClient = "downstream";

    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        // The Host authenticates its callers itself. JwtBearer reads its authority, audiences and signing keys from
        // the configuration section Authentication:Schemes:Bearer; `dotnet user-jwts` fills it for development.
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        builder.Services.AddAuthorization();
        // Authenticated by default: AddPouryaKitWeb sets the authorization fallback policy; a public endpoint says so.
        builder.Services.AddPouryaKitWeb();
        // The peer Hosts, by their authenticated Actor claim, that may continue an Operation of theirs here
        // (PouryaKit:TrustedPeers:Peers). None are configured: every caller starts a new chain of work.
        builder.Services.Configure<PouryaKitTrustedPeerOptions>(builder.Configuration.GetSection("PouryaKit:TrustedPeers"));
        // The API versions the Modules declare, one OpenAPI document each, and the configured sunset of each version a
        // Module retires (PouryaKit:OpenApi:Sunsets).
        builder.Services.AddPouryaKitOpenApi();
        builder.Services.Configure<PouryaKitOpenApiOptions>(builder.Configuration.GetSection("PouryaKit:OpenApi"));
        // Outgoing calls retry transient failures of safe methods only and carry the Operation Context they are sent
        // with, never the caller's bearer token. A peer that authenticates this Host gets the Host's own credentials.
        var downstream = builder.Services.AddHttpClient(DownstreamClient);
        downstream.AddPouryaKitResilience();
        downstream.AddPouryaKitOperationContext();
        // The permissions a command or query declares (Catalog's RemoveProducts) are the HTTP caller's permission claims.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<IActorPermissions, CallerPermissions>();
        builder.AddPouryaKitModules(modules =>
        {
            modules.Add<CatalogModule>();
            modules.Add<InventoryModule>();
            modules.Add<BillingModule>();
        });
        // An activity and a duration for every command and query, around the dispatcher the Modules registered. The
        // Host's exporter, when it has one, listens to OperationTelemetry.SourceName; the Kit exports nothing.
        builder.Services.AddPouryaKitObservability();
        builder.UsePouryaKitWolverine(connectionName: "app");
        builder.Services.AddSingleton<INewProductAnnouncer, LoggedNewProductAnnouncer>();
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
        // The documents and their Scalar reference are for developers, and public there.
        if (app.Environment.IsDevelopment())
        {
            app.MapPouryaKitOpenApi().AllowAnonymous();
        }

        return app;
    }
}
