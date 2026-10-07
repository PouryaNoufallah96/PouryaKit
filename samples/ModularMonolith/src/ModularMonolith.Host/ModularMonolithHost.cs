using Billing;
using Catalog;
using Catalog.Products.Events.Notifications;
using Inventory;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Modularity;
using PouryaKit.Security;
using PouryaKit.Security.JwtBearer;
using PouryaKit.Web;

namespace ModularMonolith.Host;

/// <summary>The Host's composition, shared by <c>Program</c> and the tests that host the application.</summary>
public static class ModularMonolithHost
{
    /// <summary>The named HTTP client for calls to another Host.</summary>
    public const string DownstreamClient = "downstream";

    /// <summary>The claim of a caller's bearer token that names its tenant.</summary>
    public const string TenantClaim = "tenant";

    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        // The Host authenticates its callers with bearer tokens. The issuer, audiences and signing keys come from the
        // configuration section Authentication:Schemes:Bearer (`dotnet user-jwts` fills it for development); without
        // them the Host does not start. A token's "sub" is the Actor and its "tenant" the tenant.
        builder.Services.AddPouryaKitJwtBearer(claims => claims.TenantClaim = TenantClaim);
        builder.Services.AddAuthorization();
        // Authenticated by default: AddPouryaKitWeb sets the authorization fallback policy; a public endpoint says so.
        // The Operation's tenant is the one the bearer scheme verified.
        builder.Services.AddPouryaKitWeb(web => web.TenantClaim = PouryaKitClaimTypes.Tenant);
        // The peer Hosts, by their authenticated Actor claim, that may continue an Operation of theirs here
        // (PouryaKit:TrustedPeers:Peers). None are configured: every caller starts a new chain of work.
        builder.Services.Configure<PouryaKitTrustedPeerOptions>(builder.Configuration.GetSection("PouryaKit:TrustedPeers"));
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
        return app;
    }
}
