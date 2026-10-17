using Billing;
using Catalog;
using Catalog.Products.Events.Notifications;
using Inventory;
using PouryaKit.Caching;
using PouryaKit.Cqrs;
using PouryaKit.Grpc;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Messaging.Wolverine.RabbitMq;
using PouryaKit.Modularity;
using PouryaKit.Observability;
using PouryaKit.Security;
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
        // Host does not start. A token's "sub" is the Actor and its "tenant" the tenant.
        builder.Services.AddPouryaKitJwtBearer(claims => claims.TenantClaim = "tenant");
        builder.Services.AddAuthorization();
        // Authenticated by default: AddPouryaKitWeb sets the authorization fallback policy; a public endpoint says so.
        builder.Services.AddPouryaKitWeb();
        // The peer Hosts that may continue an Operation of theirs here, CORS policies, response compression and the rate limit
        // per Actor, the configured sunset of each API version, and how long idempotency keys are kept. None is configured but
        // the sunset: every caller starts a new chain of work, and the Host answers no cross-origin request, compresses nothing
        // and limits no one.
        builder.Services.Configure<PouryaKitTrustedPeerOptions>(builder.Configuration.GetSection("PouryaKit:TrustedPeers"));
        builder.Services.Configure<PouryaKitCorsOptions>(builder.Configuration.GetSection("PouryaKit:Web:Cors"));
        builder.Services.Configure<PouryaKitCompressionOptions>(builder.Configuration.GetSection("PouryaKit:Web:Compression"));
        builder.Services.Configure<PouryaKitRateLimitOptions>(builder.Configuration.GetSection("PouryaKit:Web:RateLimit"));
        builder.Services.AddPouryaKitOpenApi();
        builder.Services.Configure<PouryaKitOpenApiOptions>(builder.Configuration.GetSection("PouryaKit:OpenApi"));
        builder.Services.Configure<PouryaKitIdempotencyOptions>(builder.Configuration.GetSection("PouryaKit:Idempotency"));
        // Calls to another Host carry the Operation Context they are sent with, never the caller's bearer token. The service
        // defaults already give the client its one resilience pipeline: AddPouryaKitResilience here would stack a second, which
        // the Kit refuses when the client is created.
        builder.Services.AddHttpClient("downstream").AddPouryaKitOperationContext();
        // The permissions a command or query declares (Catalog's RemoveProducts) are the HTTP caller's permission claims.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<IActorPermissions, CallerPermissions>();
        // Each Module through its Composition Root, in the Host's one container, which is validated when it is built; the
        // Kit's composition checks stop the Host before it serves.
        builder.AddPouryaKitModules(modules =>
        {
            modules.Add<CatalogModule>();
            modules.Add<InventoryModule>();
            modules.Add<BillingModule>();
        });
        // An activity and a duration for every command and query, around the dispatcher the Modules registered.
        builder.Services.AddPouryaKitObservability();
        // Cacheable queries answer from HybridCache in this Host's memory, and from Redis when ConnectionStrings:cache names one.
        builder.Services.AddPouryaKitCaching();
        if (builder.Configuration.GetConnectionString("cache") is { } redis)
        {
            builder.Services.AddStackExchangeRedisCache(options => options.Configuration = redis);
        }

        builder.UsePouryaKitWolverine(connectionName: "app");
        // With ConnectionStrings:broker, what Catalog publishes also reaches other Hosts over RabbitMQ, from this Host's queue;
        // Inventory and Billing still receive it here.
        if (builder.Configuration.GetConnectionString("broker") is not null)
        {
            builder.UsePouryaKitRabbitMq(connectionName: "broker", serviceName: "modular-monolith");
        }

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
        app.UseCors("browser");
        app.UseAuthentication();
        app.UseAuthorization();
        // After authorization: it partitions by the authenticated Actor.
        app.UseRateLimiter();
        app.MapPouryaKitModuleEndpoints();
        // The Modules' gRPC services, at the root: authenticated, and each call an Operation, as the endpoints are.
        app.MapPouryaKitGrpcServices();
        // /alive in every environment, for an orchestrator's liveness probe: anonymous, outside the rate limit, and with no
        // request timeout or output cache, since it asks nothing outside the process. /health, which asks each database, and
        // the OpenAPI documents with their Scalar reference are for developers: mapped in Development only, and public there.
        app.MapDefaultEndpoints();
        if (app.Environment.IsDevelopment())
        {
            app.MapPouryaKitOpenApi().AllowAnonymous();
        }

        return app;
    }
}
