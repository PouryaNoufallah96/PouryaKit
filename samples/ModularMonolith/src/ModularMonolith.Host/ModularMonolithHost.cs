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

namespace ModularMonolith.Host;

/// <summary>The Host's composition, shared by <c>Program</c> and the tests that host the application.</summary>
public static class ModularMonolithHost
{
    /// <summary>The named HTTP client for calls to another Host.</summary>
    public const string DownstreamClient = "downstream";

    /// <summary>
    /// The CORS policy the pipeline answers cross-origin requests with, configured at
    /// <c>PouryaKit:Web:Cors:Policies:browser</c>; unconfigured, it allows no origin.
    /// </summary>
    public const string BrowserPolicy = "browser";

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
        // Named CORS policies, response compression and the rate limit per Actor (PouryaKit:Web). None is configured:
        // the Host answers no cross-origin request, compresses nothing and limits no one.
        builder.Services.Configure<PouryaKitCorsOptions>(builder.Configuration.GetSection("PouryaKit:Web:Cors"));
        builder.Services.Configure<PouryaKitCompressionOptions>(builder.Configuration.GetSection("PouryaKit:Web:Compression"));
        builder.Services.Configure<PouryaKitRateLimitOptions>(builder.Configuration.GetSection("PouryaKit:Web:RateLimit"));
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
        // Before authentication, so a preflight is answered without credentials.
        app.UseCors(BrowserPolicy);
        app.UseAuthentication();
        app.UseAuthorization();
        // After authorization: it partitions by the authenticated Actor.
        app.UseRateLimiter();
        app.MapPouryaKitModuleEndpoints();
        return app;
    }
}
