using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using Inventory.Host;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModularMonolith.ReverseProxy;
using PouryaKit.Cqrs;
using PouryaKit.Messaging;
using PouryaKit.Observability;
using PouryaKit.Testing;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Model;

namespace ModularMonolith.Tests;

// The sample's Reverse proxy, served in memory, in front of ModularMonolith.Host on a port of its own, so YARP forwards
// over the network as deployed. The Host composes the service defaults' health checks, whose liveness path the proxy
// probes, and its bearer scheme. Both run in this process, so one listener sees the proxy's spans and the Host's. The
// service route fronts Inventory.Host the same way, on the broker fixture, whose Hosts run beside no other test.
[Collection<BrokerCollection>]
public sealed class ReverseProxyTests(AppFixture app, BrokerFixture broker)
    : HostTest<AppFixture>(app)
{
    private const string Service = "modular-monolith";

    private const string CatchAll = "{**catch-all}";

    // The route's one destination names the service; discovery resolves it into both endpoints its configuration lists.
    [Fact]
    public async Task A_service_resolves_by_discovery_and_an_endpoint_whose_liveness_probe_fails_drops_out_of_its_cluster()
    {
        await using var host = await StartHostAsync();
        var live = AddressOf(host);
        var dead = UnboundAddress();
        // A probe each second, so the dead endpoint's two failures come soon.
        await using var proxy = await StartProxyAsync(
            Service,
            CatchAll,
            [live, dead],
            $"--ReverseProxy:Clusters:{Service}:HealthCheck:Active:Interval=00:00:01"
        );

        var cluster = await ProbedAsync(proxy);
        using var client = proxy.GetTestClient();
        List<HttpStatusCode> answers = [];
        for (var i = 0; i < 4; i++)
        {
            answers.Add(
                (
                    await client.GetAsync(
                        "/stub/public/context",
                        TestContext.Current.CancellationToken
                    )
                ).StatusCode
            );
        }

        Assert.Equal(
            [(live, DestinationHealth.Healthy), (dead, DestinationHealth.Unhealthy)],
            cluster
                .Destinations.Values.Select(destination =>
                    (
                        Address: new Uri(destination.Model.Config.Address),
                        Health: destination.Health.Active
                    )
                )
                .OrderBy(probed => probed.Health)
        );
        Assert.Equal(
            [live],
            cluster.DestinationsState.AvailableDestinations.Select(destination => new Uri(
                destination.Model.Config.Address
            ))
        );
        Assert.All(answers, answer => Assert.Equal(HttpStatusCode.OK, answer));
    }

    // YARP's default would forward to every destination again once all of them are out; the Kit keeps them out, so the
    // cluster answers 503 until a probe succeeds. The Host's own liveness check is switched, so the Host itself still
    // answers 200 throughout: a 503 is the proxy's, not the Host's.
    [Fact]
    public async Task A_cluster_whose_every_destination_fails_its_probe_answers_503_until_a_probe_succeeds_again()
    {
        var alive = true;
        await using var host = await StartHostAsync(builder =>
            builder
                .Services.AddHealthChecks()
                .AddCheck(
                    "switch",
                    () => alive ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy(),
                    ["live"]
                )
        );
        await using var proxy = await StartProxyAsync(
            Service,
            CatchAll,
            [AddressOf(host)],
            $"--ReverseProxy:Clusters:{Service}:HealthCheck:Active:Interval=00:00:01"
        );
        using var client = proxy.GetTestClient();
        await ProbedAsync(proxy, DestinationHealth.Healthy);

        alive = false;
        await ProbedAsync(proxy, DestinationHealth.Unhealthy);
        var whileOut = (
            await client.GetAsync("/stub/public/context", TestContext.Current.CancellationToken)
        ).StatusCode;
        alive = true;
        await ProbedAsync(proxy, DestinationHealth.Healthy);
        var afterRecovery = (
            await client.GetAsync("/stub/public/context", TestContext.Current.CancellationToken)
        ).StatusCode;

        Assert.Equal(
            (HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK),
            (whileOut, afterRecovery)
        );
    }

    // The client's W3C context arrives at the proxy; YARP's default propagator sends the Host the proxy's own span in that
    // trace, never the client's span passed through nor a trace of its own. The service defaults' HttpClient
    // instrumentation injects the same context again, with OpenTelemetry's default propagator.
    [Fact]
    public async Task A_request_keeps_the_client_s_trace_and_the_host_s_operation_is_a_child_of_the_proxy_s_span_with_no_operation_in_the_proxy()
    {
        await using var host = await StartHostAsync();
        await using var proxy = await StartProxyAsync(Service, CatchAll, [AddressOf(host)]);
        using var telemetry = new RecordedTelemetry(
            durations: false,
            sources: ["Microsoft.AspNetCore", "System.Net.Http"]
        );
        var trace = ActivityTraceId.CreateRandom();
        var caller = ActivitySpanId.CreateRandom();
        using var client = proxy.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/stub/public/context");
        request.Headers.Add("traceparent", $"00-{trace}-{caller}-01");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var spans = await SpansAsync(telemetry, trace, caller);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var operation = Assert.Single(
            spans,
            span => span.Source.Name == OperationTelemetry.SourceName
        );
        List<Activity> ancestors = [];
        for (
            var parent = operation.ParentSpanId;
            spans.SingleOrDefault(span => span.SpanId == parent) is { } span;
            parent = span.ParentSpanId
        )
        {
            ancestors.Add(span);
        }

        // The Host's request, the proxy's forwarding call, the proxy's request, whose parent is the client's span.
        Assert.Equal(
            [
                ("Microsoft.AspNetCore", ActivityKind.Server),
                ("System.Net.Http", ActivityKind.Client),
                ("Microsoft.AspNetCore", ActivityKind.Server),
            ],
            ancestors.Select(span => (span.Source.Name, span.Kind))
        );
        Assert.Equal(caller, ancestors[^1].ParentSpanId);
    }

    // A Host continues an Operation only for an authenticated peer: the proxy strips neither the peer's token nor the
    // Kit's headers, and the Host authenticates the peer itself.
    [Fact]
    public async Task The_client_s_bearer_token_and_the_kit_s_headers_reach_the_host_as_sent_so_a_trusted_peer_continues_its_operation()
    {
        var correlation = Guid.CreateVersion7();
        var causation = Guid.CreateVersion7();
        await using var host = await StartHostAsync();
        await using var proxy = await StartProxyAsync(Service, CatchAll, [AddressOf(host)]);
        using var client = proxy.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            JwtBearerDefaults.AuthenticationScheme,
            TestIssuer.Token(new() { ["sub"] = AppFixture.TrustedPeer })
        );
        client.DefaultRequestHeaders.Add(OperationContextHeaders.ActorId, "ada");
        client.DefaultRequestHeaders.Add(
            OperationContextHeaders.ActorKind,
            nameof(ActorKind.Person)
        );
        client.DefaultRequestHeaders.Add(OperationContextHeaders.Tenant, "acme");
        client.DefaultRequestHeaders.Add(
            OperationContextHeaders.CorrelationId,
            correlation.ToString()
        );
        client.DefaultRequestHeaders.Add(OperationContextHeaders.CausationId, causation.ToString());

        var response = await client.GetAsync(
            "/stub/context",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var seen = (
            await response.Content.ReadFromJsonAsync<SeenContext>(
                TestContext.Current.CancellationToken
            )
        )!;
        Assert.Equal(
            (new SeenActor("ada", ActorKind.Person), "acme", correlation, causation),
            (seen.Actor, seen.Tenant, seen.CorrelationId, seen.CausationId)
        );
    }

    // Story 19 of spec #161: the sample AppHost's service route, Inventory's paths to the service named inventory, which
    // discovery resolves into Inventory.Host's endpoint. Its GetStockItem answers the stock item a delivery opened for the
    // token's tenant, and its pipeline receives the token and the Kit's headers as the client sent them.
    [Fact]
    public async Task A_request_on_the_service_route_reaches_get_stock_item_in_inventory_host_with_the_bearer_token_and_the_kit_s_headers_as_sent()
    {
        var environment = await broker.CreateEnvironmentAsync();
        var arrived = new ArrivedHeaders();
        using var inventory = await environment.StartInventoryHostAsync(configure: builder =>
        {
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton<IStartupFilter>(arrived);
        });
        await using var catalog = await environment.StartCatalogHostAsync();
        var product = Guid.CreateVersion7();
        await BrokerEnvironment.CreateProductAsync(
            catalog,
            product,
            "Lamp",
            new OperationContext(GatewayTestExtensions.TestActor, "north")
        );
        await BrokerEnvironment.WaitUntilAsync(
            environment.Inventory,
            $"exists (select from inventory.stock_items where product_id = '{product}')",
            "Inventory.Host never stocked the product."
        );
        await BrokerEnvironment.WaitUntilSettledAsync(environment.Inventory);
        await using var proxy = await StartProxyAsync(
            "inventory",
            "/inventory/{**catch-all}",
            [AddressOf((WebApplication)inventory)]
        );
        using var client = proxy.GetTestClient();
        var token = TestIssuer.Token(
            new() { ["sub"] = "ada", [InventoryHost.TenantClaim] = "north" }
        );
        var correlation = Guid.CreateVersion7().ToString();
        var causation = Guid.CreateVersion7().ToString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            JwtBearerDefaults.AuthenticationScheme,
            token
        );
        client.DefaultRequestHeaders.Add(OperationContextHeaders.ActorId, "ada");
        client.DefaultRequestHeaders.Add(
            OperationContextHeaders.ActorKind,
            nameof(ActorKind.Person)
        );
        client.DefaultRequestHeaders.Add(OperationContextHeaders.Tenant, "north");
        client.DefaultRequestHeaders.Add(OperationContextHeaders.CorrelationId, correlation);
        client.DefaultRequestHeaders.Add(OperationContextHeaders.CausationId, causation);

        using var response = await client.GetAsync(
            $"/inventory/v2/stock-items/{product}",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            $$"""{"productId":"{{product}}","quantityOnHand":0}""",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        Assert.Equal(
            [$"Bearer {token}", "ada", "Person", "north", correlation, causation],
            Assert.Single(arrived.Requests)
        );
    }

    // The sample Host with its own bearer scheme, as JwtBearerTests composes it, and the service defaults' health checks
    // and endpoints, served by Kestrel on a port of the system's choosing in place of AppFixture's in-memory server.
    private async Task<WebApplication> StartHostAsync(
        Action<WebApplicationBuilder>? configure = null
    )
    {
        var host = AppFixture.ComposeHost(
            "Development",
            Fixture.ConnectionString,
            new RecordedLogs(),
            builder =>
            {
                builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
                builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme);
                builder.AddDefaultHealthChecks();
                configure?.Invoke(builder);
            }
        );
        host.MapDefaultEndpoints();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    // One route of path to the cluster of service, its destination named by the service alone, and the service's
    // endpoints in the Services section, where AddServiceDiscovery of the service defaults reads them.
    private static async Task<WebApplication> StartProxyAsync(
        string service,
        string path,
        Uri[] endpoints,
        params string[] arguments
    )
    {
        var builder = ReverseProxyHost.CreateBuilder([
            "--environment",
            "Development",
            $"--ReverseProxy:Routes:{service}:ClusterId={service}",
            $"--ReverseProxy:Routes:{service}:Match:Path={path}",
            $"--ReverseProxy:Clusters:{service}:Destinations:host:Address=http://{service}",
            .. endpoints.Select(
                (endpoint, index) => $"--Services:{service}:http:{index}={endpoint}"
            ),
            .. arguments,
        ]);
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        var proxy = ReverseProxyHost.Build(builder);
        await proxy.StartAsync(TestContext.Current.CancellationToken);
        return proxy;
    }

    private static Uri AddressOf(WebApplication host) => new(host.Urls.Single());

    // The Authorization header and the Kit's headers of each request on Inventory's paths, as Inventory.Host's pipeline
    // receives them, before any of its middleware; the proxy's liveness probes are not among them.
    private sealed class ArrivedHeaders : IStartupFilter
    {
        private static readonly string[] Names =
        [
            "Authorization",
            OperationContextHeaders.ActorId,
            OperationContextHeaders.ActorKind,
            OperationContextHeaders.Tenant,
            OperationContextHeaders.CorrelationId,
            OperationContextHeaders.CausationId,
        ];

        public ConcurrentQueue<string[]> Requests { get; } = new();

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(
                    (context, pipeline) =>
                    {
                        if (
                            context.Request.Path.StartsWithSegments(
                                "/inventory",
                                StringComparison.Ordinal
                            )
                        )
                        {
                            Requests.Enqueue([
                                .. Names.Select(name => context.Request.Headers[name].ToString()),
                            ]);
                        }

                        return pipeline(context);
                    }
                );
                next(app);
            };
    }

    // A port nothing listens on: the system chose it, and it is free again.
    private static Uri UnboundAddress()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
    }

    // The proxy's cluster once its probes have judged every destination, each as healthy as given when one is.
    private static async Task<ClusterState> ProbedAsync(
        WebApplication proxy,
        DestinationHealth? health = null
    )
    {
        var lookup = proxy.Services.GetRequiredService<IProxyStateLookup>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            Assert.True(lookup.TryGetCluster(Service, out var cluster));
            if (
                cluster.Destinations.Values.All(destination =>
                    health is { } judged
                        ? destination.Health.Active == judged
                        : destination.Health.Active != DestinationHealth.Unknown
                )
            )
            {
                return cluster;
            }

            Assert.True(
                DateTime.UtcNow < deadline,
                $"The proxy never probed every destination: {string.Join(", ", cluster.Destinations.Values.Select(destination => $"{destination.Model.Config.Address} {destination.Health.Active}"))}."
            );
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    // The trace's spans once the proxy's own request, the one whose parent is the client's span, has stopped: it stops last.
    private static async Task<IReadOnlyList<Activity>> SpansAsync(
        RecordedTelemetry telemetry,
        ActivityTraceId trace,
        ActivitySpanId caller
    )
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (telemetry.Of(trace) is var spans && !spans.Any(span => span.ParentSpanId == caller))
        {
            Assert.True(DateTime.UtcNow < deadline, "The proxy's request never stopped.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return telemetry.Of(trace);
    }
}
