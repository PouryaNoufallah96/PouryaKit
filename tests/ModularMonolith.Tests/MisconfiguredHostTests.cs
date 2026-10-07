using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModularMonolith.Tests.Stub;
using ModularMonolith.Tests.Unowned;
using ModularMonolith.Tests.Unversioned;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using PouryaKit.Persistence.Dapper;
using PouryaKit.Persistence.Postgres;
using PouryaKit.Testing;
using PouryaKit.Validation;
using PouryaKit.Web;

namespace ModularMonolith.Tests;

// A Host composed wrongly in a way that would otherwise run degraded and say nothing fails where the mistake shows, with a
// message naming the fix (ticket #62): when it is built, at start, when its endpoint is built, when its client is first
// created, or at the first request.
public sealed class MisconfiguredHostTests(AppFixture app) : HostTest<AppFixture>(app)
{
    // WebApplication adds UseAuthentication ahead of the application's own middleware unless the application calls it,
    // so the order is wrong only when the application calls it after UseRateLimiter.
    [Fact]
    public async Task A_rate_limiter_placed_before_authentication_fails_the_request_instead_of_putting_every_caller_in_one_partition()
    {
        var builder = WebOnly();
        builder.Services.Configure<PouryaKitRateLimitOptions>(limit =>
        {
            limit.Enabled = true;
            limit.PermitLimit = 1;
            limit.Window = TimeSpan.FromMinutes(10);
        });
        await using var host = builder.Build();
        host.UseRateLimiter();
        host.UseAuthentication();
        host.UseAuthorization();
        host.MapGet("/limited", () => "served");
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var ada = AppFixture.ClientOf(host, "ada");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => ada.GetAsync("/limited", TestContext.Current.CancellationToken));

        Assert.Equal(
            "The rate limit partitions each request by its Actor, but the request reached the rate limiter before authentication ran, so every caller would share one partition. Call app.UseAuthentication() before app.UseRateLimiter(), or leave UseAuthentication to WebApplication, which adds it ahead of the application's own middleware.",
            thrown.Message
        );
    }

    // The service defaults give every client the Kit's pipeline; this client adds its own on top instead of in its place.
    [Fact]
    public void A_client_that_adds_a_resilience_pipeline_over_the_service_defaults_one_fails_whenever_it_is_created()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.AddServiceDefaults();
        builder.Services.AddHttpClient("downstream").AddPouryaKitResilience();
        using var host = builder.Build();
        var clients = host.Services.GetRequiredService<IHttpClientFactory>();

        var thrown = Assert.Throws<InvalidOperationException>(() => clients.CreateClient("downstream"));
        var again = Assert.Throws<InvalidOperationException>(() => clients.CreateClient("downstream"));

        Assert.Equal(
            "The HTTP client 'downstream' has 2 resilience handlers, so each would retry the other's attempts, as when the defaults of every client add one and the client adds its own. Call RemoveAllResilienceHandlers() on the client before AddPouryaKitResilience(), or remove the client's own.",
            thrown.Message
        );
        Assert.Equal(thrown.Message, again.Message);
    }

    [Fact]
    public async Task An_endpoint_that_requires_an_idempotency_key_on_a_get_fails_the_host_at_start()
    {
        await using var host = AppFixture.ComposeHost(
            "Production",
            Fixture.ConnectionString,
            new RecordedLogs(),
            builder => builder.Services.AddSingleton<KeyedReadRoute>()
        );

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            "HTTP: GET /stub/keyed-read requires an Idempotency-Key on GET, a method that only reads, and a query ignores the key. Remove RequireIdempotencyKey(), or map the command on POST, PUT, PATCH or DELETE.",
            thrown.Message
        );
    }

    // Nothing connects: the Host has no database to reach.
    [Fact]
    public void A_connection_string_the_configuration_lacks_or_leaves_blank_fails_the_host_when_built_naming_its_key()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.Configuration.AddInMemoryCollection([new("ConnectionStrings:reports", " ")]);
        // The sample Host's appsettings.json, copied beside the tests, sets ConnectionStrings:app.
        builder.Services.UsePostgres<StubDbContext>(connectionName: "ledger", schema: "stub");
        builder.Services.AddPouryaKitDapper(connectionName: "reports");
        builder.AddPouryaKitCompositionChecks();

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(
            [
                "UsePostgres stores StubDbContext over the connection string ledger, which the Host's configuration does not set (ConnectionStrings:ledger), so every Operation that opens it would fail. Set ConnectionStrings:ledger, or name in UsePostgres a connection the Host configures.",
                "Dapper reads use the connection string reports, which the Host's configuration does not set (ConnectionStrings:reports), so every read would fail. Set ConnectionStrings:reports, or name in AddPouryaKitDapper a connection the Host configures.",
            ],
            thrown.Message.Split(Environment.NewLine)
        );
    }

    [Fact]
    public void A_module_whose_notification_handlers_no_registration_declares_fails_the_host_when_built_naming_the_call()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            AppFixture.ComposeHost(
                "Development",
                Fixture.ConnectionString,
                new RecordedLogs(),
                builder => builder.AddPouryaKitModules(modules => modules.Add<HandlerForgettingModule>())
            )
        );

        Assert.Equal(
            $"The Notification Handlers {typeof(UnownedNotificationHandler).FullName} of HandlerForgettingModule are declared by no AddPouryaKitNotificationHandlers, so they never run. Call AddPouryaKitNotificationHandlers() in its Composition Root.",
            thrown.Message
        );
    }

    // A startup check: the application maps its routes after the Host is built.
    [Fact]
    public async Task A_module_whose_endpoints_no_registration_declares_stops_the_host_at_start_naming_the_call()
    {
        await using var host = AppFixture.ComposeHost(
            "Development",
            Fixture.ConnectionString,
            new RecordedLogs(),
            builder => builder.AddPouryaKitModules(modules => modules.Add<EndpointForgettingModule>())
        );

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            $"The endpoints {typeof(UnversionedEndpoints).Namespace}.ReadActorEndpoint of EndpointForgettingModule are declared by no AddPouryaKitEndpoints, so they never answer. Call AddPouryaKitEndpoints(routePrefix) in its Composition Root.",
            thrown.Message
        );
    }

    [Fact]
    public async Task Endpoints_declared_but_never_mapped_stop_the_host_at_start_naming_the_call()
    {
        var builder = WebOnly();
        builder.Services.AddPouryaKitEndpoints("/plain", typeof(UnversionedEndpoints).Assembly);
        builder.AddPouryaKitCompositionChecks();
        await using var host = builder.Build();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            $"The endpoints {typeof(UnversionedEndpoints).Namespace}.ReadActorEndpoint of the application are declared but never mapped, so they never answer. Call app.MapPouryaKitModuleEndpoints() once the Host is built.",
            thrown.Message
        );
    }

    // A Web Host with no database, authenticating with the tests' scheme, whose pipeline each test composes.
    private static WebApplicationBuilder WebOnly()
    {
        var builder = WebApplication.CreateBuilder(["--environment", "Production"]);
        builder.WebHost.UseTestServer();
        builder
            .Services.AddAuthentication(TestAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthentication>(TestAuthentication.SchemeName, null);
        builder.Services.AddAuthorization();
        builder.Services.AddPouryaKitWeb();
        return builder;
    }
}

// Registers the Features of an assembly with an endpoint, but declares no endpoint.
public sealed class EndpointForgettingModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module) => module.AddPouryaKitCqrs(typeof(UnversionedEndpoints).Assembly);
}

// Registers the validators of an assembly with a Notification Handler, but declares no Notification Handler.
public sealed class HandlerForgettingModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module) => module.AddPouryaKitValidation(typeof(UnownedNotificationHandler).Assembly);
}
