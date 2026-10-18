using Billing;
using Catalog;
using Catalog.Products.Events.Notifications;
using Inventory;
using PouryaKit.AI;
using PouryaKit.Caching;
using PouryaKit.Cqrs;
using PouryaKit.Email.MailKit;
using PouryaKit.Grpc;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Messaging.Wolverine.RabbitMq;
using PouryaKit.Modularity;
using PouryaKit.Observability;
using PouryaKit.Realtime;
using PouryaKit.Reporting.Module;
using PouryaKit.Security;
using PouryaKit.Security.JwtBearer;
using PouryaKit.Storage.S3;
using PouryaKit.Web;
using PouryaKit.Web.OpenApi;

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

    /// <summary>The claim of a caller's bearer token that names its tenant.</summary>
    public const string TenantClaim = "tenant";

    /// <summary>
    /// The connection string of the RabbitMQ broker. Configured, the Host sends what Catalog publishes to other Hosts
    /// over it, and Inventory runs in its own Host, <c>Inventory.Host</c>; absent, every delivery stays in this Host.
    /// </summary>
    public const string BrokerConnection = "broker";

    /// <summary>The Host's name on the broker: its queue, unique to this kind of Host.</summary>
    public const string ServiceName = "modular-monolith";

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
        builder.Services.AddPouryaKitWeb();
        // The peer Hosts, by their authenticated Actor claim, that may continue an Operation of theirs here
        // (PouryaKit:TrustedPeers:Peers). None are configured: every caller starts a new chain of work.
        builder.Services.Configure<PouryaKitTrustedPeerOptions>(builder.Configuration.GetSection("PouryaKit:TrustedPeers"));
        // Named CORS policies, response compression and the rate limit per Actor (PouryaKit:Web). None is configured:
        // the Host answers no cross-origin request, compresses nothing and limits no one.
        builder.Services.Configure<PouryaKitCorsOptions>(builder.Configuration.GetSection("PouryaKit:Web:Cors"));
        builder.Services.Configure<PouryaKitCompressionOptions>(builder.Configuration.GetSection("PouryaKit:Web:Compression"));
        builder.Services.Configure<PouryaKitRateLimitOptions>(builder.Configuration.GetSection("PouryaKit:Web:RateLimit"));
        // The API versions the Modules declare, one OpenAPI document each, and the configured sunset of each version a
        // Module retires (PouryaKit:OpenApi:Sunsets).
        builder.Services.AddPouryaKitOpenApi();
        builder.Services.Configure<PouryaKitOpenApiOptions>(builder.Configuration.GetSection("PouryaKit:OpenApi"));
        // How long an idempotent request's key is kept, and how its expired keys are removed (PouryaKit:Idempotency).
        // Nothing is configured: 24 hours, cleaned up every hour in batches of 1000.
        builder.Services.Configure<PouryaKitIdempotencyOptions>(builder.Configuration.GetSection("PouryaKit:Idempotency"));
        // Outgoing calls retry transient failures of safe methods only and carry the Operation Context they are sent
        // with, never the caller's bearer token. A peer that authenticates this Host gets the Host's own credentials.
        var downstream = builder.Services.AddHttpClient(DownstreamClient);
        downstream.AddPouryaKitResilience();
        downstream.AddPouryaKitOperationContext();
        // The permissions a command or query declares (Catalog's RemoveProducts) are the HTTP caller's permission claims.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<IActorPermissions, CallerPermissions>();
        var broker = builder.Configuration.GetConnectionString(BrokerConnection) is not null;
        builder.AddPouryaKitModules(modules =>
        {
            modules.Add<CatalogModule>();
            // With a broker, Inventory is its own Host, which receives what Catalog publishes here.
            if (!broker)
            {
                modules.Add<InventoryModule>();
            }

            modules.Add<BillingModule>();
            modules.Add<ReportModule>();
        });
        // An activity and a duration for every command and query, around the dispatcher the Modules registered. The
        // Host's exporter, when it has one, listens to OperationTelemetry.SourceName; the Kit exports nothing.
        builder.Services.AddPouryaKitObservability();
        // Cacheable queries, such as Catalog's GetProduct, answer from HybridCache in this Host's memory, and also from
        // Redis when ConnectionStrings:cache names one, so that every Host of the application reads what one stored.
        builder.Services.AddPouryaKitCaching();
        if (builder.Configuration.GetConnectionString("cache") is { } redis)
        {
            builder.Services.AddStackExchangeRedisCache(options => options.Configuration = redis);
        }

        // Email a Notification Handler sends through IEmailSender goes out through MailKit to the SMTP server at
        // PouryaKit:Email; appsettings.json names a local Papercut, which keeps every mail for a developer to read.
        builder.Services.AddPouryaKitMailKit();
        builder.Services.Configure<PouryaKitMailKitOptions>(builder.Configuration.GetSection("PouryaKit:Email"));
        // Files go to the S3 bucket of PouryaKit:Storage:S3; appsettings.json names one of a local S3-compatible store.
        builder.Services.Configure<PouryaKitS3StorageOptions>(builder.Configuration.GetSection("PouryaKit:Storage:S3"));
        builder.Services.AddPouryaKitS3Storage();
        // The report Module answers POST /reports with a job, generated in the background and stored there: the Host
        // decides who may request which report and lays each kind out (SalesReports), and PouryaKit:Reports sets how long
        // a job is kept. Nothing is configured: seven days, cleaned up every hour.
        builder.Services.Configure<PouryaKitReportOptions>(builder.Configuration.GetSection("PouryaKit:Reports"));
        builder.Services.AddSingleton<IReportRequestAuthorizer, SalesReports>();
        builder.Services.AddSingleton<IReportLayouts, SalesReports>();
        // Catalog's agent asks the model of PouryaKit:AI through the Host's one chat client; appsettings.json names a local
        // Ollama, and GetProduct as the one tool a run may be given.
        builder.Services.AddPouryaKitAI();
        builder.Services.Configure<PouryaKitAIOptions>(builder.Configuration.GetSection("PouryaKit:AI"));
        // What a Notification Handler pushes through IRealtimePublisher, such as Catalog's new products, reaches the
        // browsers connected to Catalog's SSE endpoint on any instance of the Host, through PostgreSQL's notifications in
        // the database every Module stores in.
        builder.Services.AddPouryaKitRealtime(connectionName: "app");
        builder.UsePouryaKitWolverine(connectionName: "app");
        if (broker)
        {
            builder.UsePouryaKitRabbitMq(BrokerConnection, ServiceName);
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
        app.UseCors(BrowserPolicy);
        app.UseAuthentication();
        app.UseAuthorization();
        // After authorization: it partitions by the authenticated Actor.
        app.UseRateLimiter();
        app.MapPouryaKitModuleEndpoints();
        // The Modules' gRPC services, at the root: authenticated, and each call an Operation, as the endpoints are.
        app.MapPouryaKitGrpcServices();
        // The documents and their Scalar reference are for developers, and public there.
        if (app.Environment.IsDevelopment())
        {
            app.MapPouryaKitOpenApi().AllowAnonymous();
        }

        return app;
    }
}
