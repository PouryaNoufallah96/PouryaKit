using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Polly;
using PouryaKit.Cqrs;
using PouryaKit.Messaging;
using PouryaKit.Observability;
using PouryaKit.Web;
using Serilog.Core;
using Serilog.Events;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ModularMonolith.Tests;

// The service defaults of ModularMonolith.ServiceDefaults, template content and not a Kit package, composed with the
// Kit: in Inventory.Host on the broker fixture, and in a Web Host of its own for outgoing HTTP against WireMock.Net.
[Collection<BrokerCollection>]
public sealed class HostRecipeTests(BrokerFixture broker) : IDisposable
{
    private const string StockItems = "inventory.product-created";
    private const string Client = "downstream";

    private static readonly OperationContext Context = new(Actor.Person("ada"), "acme");

    // ASP.NET Core's boundaries for http.server.request.duration, in seconds.
    private static readonly double[] SecondScale =
    [
        0.005,
        0.01,
        0.025,
        0.05,
        0.075,
        0.1,
        0.25,
        0.5,
        0.75,
        1,
        2.5,
        5,
        7.5,
        10,
    ];

    private readonly WireMockServer _downstream = WireMockServer.Start();

    public void Dispose()
    {
        _downstream.Stop();
        _downstream.Dispose();
    }

    // What the service defaults export, seen by an exporter of the test's own added to their providers: no backend.
    [Fact]
    public async Task Inventory_hosts_deliveries_reach_the_exporters_of_its_service_defaults()
    {
        var environment = await broker.CreateEnvironmentAsync();
        var exported = new ExportedTelemetry();
        using var inventory = await environment.StartInventoryHostAsync(configure: builder =>
            exported.AddTo(builder.Services)
        );
        await using var catalog = await environment.StartCatalogHostAsync();
        var product = Guid.CreateVersion7();
        var earlier = DeliveryProbes.ActivitiesOf(StockItems).Count;

        await BrokerEnvironment.CreateProductAsync(catalog, product, "Lamp");
        await StockedAsync(environment, product);
        inventory.Services.GetRequiredService<MeterProvider>().ForceFlush();

        var delivery = Assert.IsType<Activity>(
            Assert.Single(DeliveryProbes.ActivitiesOf(StockItems).Skip(earlier))
        );
        Assert.Contains(
            exported.Activities,
            activity =>
                activity.Source.Name == OperationTelemetry.SourceName
                && activity.SpanId == delivery.SpanId
        );
        // Each command the delivery's Operation sends to its database is a span of Npgsql's own source, under the Operation's.
        Assert.Contains(
            exported.Activities,
            activity => activity.Source.Name == "Npgsql" && activity.ParentSpanId == delivery.SpanId
        );
        Assert.Contains(
            exported.Durations,
            duration =>
                (duration.Module, duration.Operation, duration.Outcome)
                    == ("InventoryModule", "Deliver", OperationTelemetry.Succeeded)
                && duration.Count > 0
                && duration.Sum > 0
        );
        // The histogram's own bucket boundaries, which the exporter takes with no view of the Host's.
        Assert.All(exported.Durations, duration => Assert.Equal(SecondScale, duration.Boundaries));
    }

    // The database stops taking connections while the Host runs: alter database ... allow_connections false, and its
    // open sessions ended, until the test allows them again.
    [Fact]
    public async Task Liveness_stays_healthy_while_readiness_reports_the_database_the_host_cannot_reach()
    {
        var environment = await broker.CreateEnvironmentAsync();
        using var inventory = await environment.StartInventoryHostAsync();
        using var client = inventory.GetTestClient();
        var database = new NpgsqlConnectionStringBuilder(environment.Inventory).Database;
        var server = new NpgsqlConnectionStringBuilder(environment.Inventory)
        {
            Database = "postgres",
        }.ConnectionString;
        List<(HttpStatusCode, HttpStatusCode)> answers = [await HealthAsync(client)];

        await Sql.RowsAsync(
            server,
            $"alter database {database} with allow_connections false; select pg_terminate_backend(pid) from pg_stat_activity where datname = '{database}'"
        );
        try
        {
            answers.Add(await HealthAsync(client));
        }
        finally
        {
            await Sql.RowsAsync(
                server,
                $"alter database {database} with allow_connections true; select 1"
            );
        }

        answers.Add(await HealthAsync(client));

        Assert.Equal(
            [
                (HttpStatusCode.OK, HttpStatusCode.OK),
                (HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable),
                (HttpStatusCode.OK, HttpStatusCode.OK),
            ],
            answers
        );
    }

    // Every other /alive test runs in Development, where both endpoints are mapped. This Host authenticates every endpoint
    // by default, so /alive answers only where it is mapped and allows anonymous callers, and /health, which no endpoint
    // serves here, answers the fallback policy's 401.
    [Fact]
    public async Task Outside_development_liveness_answers_an_anonymous_caller_and_readiness_is_not_served()
    {
        await using var host = ComposeProductionWebHost();
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var anonymous = host.GetTestClient();

        using var alive = await anonymous.GetAsync("/alive", TestContext.Current.CancellationToken);
        using var health = await anonymous.GetAsync(
            "/health",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            (HttpStatusCode.OK, "Healthy", HttpStatusCode.Unauthorized),
            (
                alive.StatusCode,
                await alive.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
                health.StatusCode
            )
        );
    }

    // An anonymous caller of an endpoint outside every Module resolves no Actor, so the Kit's limiter counts it in the
    // one partition of such callers, which a liveness probe would share. The partition's one permit is spent before the
    // probe arrives, and still refuses the next such caller after it.
    [Fact]
    public async Task Liveness_answers_while_the_rate_limit_refuses_the_other_callers_no_actor_resolves_for()
    {
        await using var host = ComposeProductionWebHost(builder =>
            builder.Services.Configure<PouryaKitRateLimitOptions>(limit =>
            {
                limit.Enabled = true;
                limit.PermitLimit = 1;
                limit.Window = TimeSpan.FromMinutes(10);
            })
        );
        host.MapGet("/public", () => "served").AllowAnonymous();
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var anonymous = host.GetTestClient();

        using var first = await anonymous.GetAsync(
            "/public",
            TestContext.Current.CancellationToken
        );
        using var alive = await anonymous.GetAsync("/alive", TestContext.Current.CancellationToken);
        using var aliveAgain = await anonymous.GetAsync(
            "/alive",
            TestContext.Current.CancellationToken
        );
        using var second = await anonymous.GetAsync(
            "/public",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            (
                HttpStatusCode.OK,
                HttpStatusCode.OK,
                HttpStatusCode.OK,
                HttpStatusCode.TooManyRequests
            ),
            (first.StatusCode, alive.StatusCode, aliveAgain.StatusCode, second.StatusCode)
        );
    }

    // As its own process in Production, Inventory.Host answers /alive to a probe that carries no token, authenticates every
    // other path by default, and has no endpoint at /health: an authenticated caller gets 404 there, where an anonymous one
    // would get the authorization fallback policy's 401.
    [Fact]
    public async Task Inventory_host_running_as_its_own_process_in_production_answers_liveness_and_serves_no_readiness()
    {
        var environment = await broker.CreateEnvironmentAsync();
        await environment.MigrateInventoryAsync();
        using var process = InventoryProcess.Start(environment);
        using var anonymous = new HttpClient
        {
            BaseAddress = await process.ListeningAsync(TestContext.Current.CancellationToken),
        };
        using var readiness = new HttpRequestMessage(HttpMethod.Get, "/health")
        {
            Headers =
            {
                Authorization = new("Bearer", TestIssuer.Token(new() { ["sub"] = "ada" })),
            },
        };

        using var alive = await anonymous.GetAsync("/alive", TestContext.Current.CancellationToken);
        using var health = await anonymous.SendAsync(
            readiness,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            (HttpStatusCode.OK, "Healthy", HttpStatusCode.NotFound),
            (
                alive.StatusCode,
                await alive.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
                health.StatusCode
            )
        );
    }

    // Serilog's logger factory replaces the logging providers, the test's RecordedLogs among them; the recipe also writes
    // to every ILogEventSink the Host registers.
    [Fact]
    public async Task With_serilog_the_kits_logs_reach_a_sink_the_host_registers()
    {
        var environment = await broker.CreateEnvironmentAsync();
        var sink = new RecordedEvents();
        using var inventory = await environment.StartInventoryHostAsync(configure: builder =>
        {
            builder.AddSerilogDefaults();
            builder.Services.AddSingleton<ILogEventSink>(sink);
        });
        await using var catalog = await environment.StartCatalogHostAsync();
        var product = Guid.CreateVersion7();
        var created = new OperationContext(Actor.Person("ada"), AppFixture.TestTenant);

        await BrokerEnvironment.CreateProductAsync(catalog, product, "Lamp", created);
        await StockedAsync(environment, product);

        var dispatched = sink
            .Events.Where(logged =>
                Scalar(logged, "SourceContext") == typeof(IDispatcher).Namespace + ".Dispatcher"
            )
            .ToList();
        Assert.Contains(
            dispatched,
            logged =>
                logged.MessageTemplate.Text == "{Operation} succeeded in {ElapsedMilliseconds} ms"
                && Scalar(logged, "Operation") == "Deliver"
        );
        // The Operation's log scope: the delivery's own id, the correlation of the CreateProduct it continues, its Module.
        var delivery = Assert.Single(
            DeliveryProbes.ContextsOf(StockItems),
            context => context.CorrelationId == created.CorrelationId
        );
        Assert.Contains(
            ("Deliver", delivery.OperationId, created.CorrelationId, "InventoryModule"),
            dispatched.Select(logged =>
                (
                    Scalar(logged, "Operation"),
                    Id(logged, "OperationId"),
                    Id(logged, "CorrelationId"),
                    Scalar(logged, "Module")
                )
            )
        );
    }

    // The service defaults give every client the Kit's standard pipeline. The downstream asks for no delay before a retry.
    [Theory]
    [InlineData("GET", 4)]
    [InlineData("POST", 1)]
    public async Task A_client_that_keeps_the_service_defaults_retries_a_read_through_one_pipeline_and_sends_a_write_once(
        string method,
        int attempts
    )
    {
        RespondUnavailable(retryAfter: "0");
        await using var host = await StartWebHostAsync(client =>
            client.AddPouryaKitOperationContext()
        );

        using var response = await SendAsync(host, new HttpMethod(method));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(attempts, _downstream.LogEntries.Count());
    }

    [Fact]
    public async Task A_client_that_replaces_the_service_defaults_pipeline_makes_its_configured_attempts_each_with_the_operation_context()
    {
        RespondUnavailable();
        await using var host = await StartWebHostAsync(client =>
            OwnPipeline(client, TimeSpan.FromMilliseconds(10))
        );

        using var response = await SendAsync(host, HttpMethod.Get);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var received = _downstream.LogEntries.Select(entry => entry.RequestMessage!).ToList();
        Assert.Equal(3, received.Count);
        Assert.All(
            received,
            request =>
                Assert.Equal(
                    new Dictionary<string, string>
                    {
                        [OperationContextHeaders.ActorId] = "ada",
                        [OperationContextHeaders.ActorKind] = nameof(ActorKind.Person),
                        [OperationContextHeaders.Tenant] = "acme",
                        [OperationContextHeaders.CorrelationId] = Context.CorrelationId.ToString(),
                        [OperationContextHeaders.CausationId] = Context.OperationId.ToString(),
                    },
                    request
                        .Headers!.Where(header =>
                            header.Key.StartsWith("pouryakit-", StringComparison.OrdinalIgnoreCase)
                        )
                        .ToDictionary(
                            header => header.Key.ToLowerInvariant(),
                            header => string.Join(",", header.Value)
                        )
                )
        );
    }

    // The unnamed client's pipeline would be the defaults' own ("-standard"), so its replay policy would become that of
    // every client that keeps the defaults.
    [Fact]
    public async Task An_unnamed_client_cannot_make_the_service_defaults_pipeline_replay_writes()
    {
        RespondUnavailable(retryAfter: "0");
        Exception? refused = null;
        await using var host = await StartWebHostAsync(client =>
        {
            client.AddPouryaKitOperationContext();
            var unnamed = client.Services.AddHttpClient(string.Empty);
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental in Microsoft.Extensions.Http.Resilience.
            unnamed.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
            refused = Record.Exception(() =>
                unnamed.AddPouryaKitResilience(HttpReplay.AllRequests)
            );
        });

        using var response = await SendAsync(host, HttpMethod.Post);

        Assert.Equal(
            (typeof(ArgumentException), 1),
            (refused?.GetType(), _downstream.LogEntries.Count())
        );
    }

    // The downstream asks for a retry after one second; the call is cancelled once the first attempt arrived, not on a
    // timer, which a slow first request could outrun.
    [Fact]
    public async Task Cancelling_a_call_through_the_service_defaults_pipeline_stops_further_attempts()
    {
        RespondUnavailable(retryAfter: "1");
        await using var host = await StartWebHostAsync(client =>
            client.AddPouryaKitOperationContext()
        );
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var call = SendAsync(host, HttpMethod.Get, cancellation.Token);
        while (!_downstream.LogEntries.Any() && !call.IsCompleted)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        // Past the first retry a call that ignored the cancellation would make.
        await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);
        Assert.Single(_downstream.LogEntries);
    }

    // The rows commit before the delivery's Operation ends, logs and records its duration: wait for Wolverine to mark
    // the envelope handled, which it does once the handler returned.
    private static async Task StockedAsync(BrokerEnvironment environment, Guid product)
    {
        await BrokerEnvironment.WaitUntilAsync(
            environment.Inventory,
            $"exists (select from inventory.pouryakit_receipts r join inventory.stock_items s on s.product_id = '{product}' where r.subscription = '{StockItems}')",
            "Inventory.Host never stocked the product."
        );
        await BrokerEnvironment.WaitUntilSettledAsync(environment.Inventory);
    }

    private static async Task<(HttpStatusCode Alive, HttpStatusCode Health)> HealthAsync(
        HttpClient client
    )
    {
        using var alive = await client.GetAsync("/alive", TestContext.Current.CancellationToken);
        using var health = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        return (alive.StatusCode, health.StatusCode);
    }

    // A Web Host of its own with the service defaults and the client, as a Host composes one, started.
    private static async Task<WebApplication> StartWebHostAsync(Action<IHttpClientBuilder> client)
    {
        var builder = WebApplication.CreateBuilder(["--environment", "Development"]);
        builder.WebHost.UseTestServer();
        builder.AddServiceDefaults();
        client(builder.Services.AddHttpClient(Client));
        var app = builder.Build();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    // A Web Host in Production with the service defaults, composed as the templates' Program.cs composes one: the tests'
    // authentication scheme in place of the bearer one, authenticated by default, the Web defaults, and the middleware in
    // the templates' order. Built, not started.
    private static WebApplication ComposeProductionWebHost(
        Action<WebApplicationBuilder>? configure = null
    )
    {
        var builder = WebApplication.CreateBuilder(["--environment", "Production"]);
        builder.WebHost.UseTestServer();
        builder.AddServiceDefaults();
        builder
            .Services.AddAuthentication(TestAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthentication>(
                TestAuthentication.SchemeName,
                null
            );
        builder.Services.AddAuthorization();
        builder.Services.AddPouryaKitWeb();
        configure?.Invoke(builder);
        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapDefaultEndpoints();
        return app;
    }

    // In place of the service defaults' pipeline, the client's own, with two retries at the given constant delay.
    private static void OwnPipeline(IHttpClientBuilder client, TimeSpan retryDelay)
    {
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental in Microsoft.Extensions.Http.Resilience.
        client.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        client
            .AddPouryaKitResilience()
            .Configure(options =>
            {
                options.Retry.MaxRetryAttempts = 2;
                options.Retry.Delay = retryDelay;
                options.Retry.BackoffType = DelayBackoffType.Constant;
                options.Retry.UseJitter = false;
            });
        client.AddPouryaKitOperationContext();
    }

    private void RespondUnavailable(string? retryAfter = null)
    {
        var response = Response.Create().WithStatusCode(HttpStatusCode.ServiceUnavailable);
        _downstream
            .Given(Request.Create().WithPath("/downstream"))
            .RespondWith(
                retryAfter is null ? response : response.WithHeader("Retry-After", retryAfter)
            );
    }

    private async Task<HttpResponseMessage> SendAsync(
        WebApplication host,
        HttpMethod method,
        CancellationToken? cancellationToken = null
    )
    {
        using var request = new HttpRequestMessage(
            method,
            $"{_downstream.Url}/downstream"
        ).WithOperationContext(Context);
        return await host
            .Services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(Client)
            .SendAsync(request, cancellationToken ?? TestContext.Current.CancellationToken);
    }

    private static string? Scalar(LogEvent logged, string property) =>
        logged.Properties.TryGetValue(property, out var value)
        && value is ScalarValue { Value: string text }
            ? text
            : null;

    private static Guid Id(LogEvent logged, string property) =>
        logged.Properties.TryGetValue(property, out var value)
        && value is ScalarValue { Value: Guid id }
            ? id
            : Guid.Empty;

    private sealed class RecordedEvents : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events => [.. _events];

        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
    }

    // An exporter for each of the service defaults' tracer and meter providers.
    private sealed class ExportedTelemetry
    {
        private readonly ConcurrentQueue<Activity> _activities = [];
        private readonly ConcurrentQueue<Duration> _durations = [];

        public IReadOnlyList<Activity> Activities => [.. _activities];

        public IReadOnlyList<Duration> Durations => [.. _durations];

        public void AddTo(IServiceCollection services)
        {
            services.ConfigureOpenTelemetryTracerProvider(tracing =>
                tracing.AddProcessor(
                    new SimpleActivityExportProcessor(new ActivityExporter(_activities))
                )
            );
            services.ConfigureOpenTelemetryMeterProvider(metrics =>
                metrics.AddReader(new BaseExportingMetricReader(new DurationExporter(_durations)))
            );
        }

        public sealed record Duration(
            string? Module,
            string? Operation,
            string? Outcome,
            long Count,
            double Sum,
            IReadOnlyList<double> Boundaries
        );

        private sealed class ActivityExporter(ConcurrentQueue<Activity> exported)
            : BaseExporter<Activity>
        {
            public override ExportResult Export(in Batch<Activity> batch)
            {
                foreach (var activity in batch)
                {
                    exported.Enqueue(activity);
                }

                return ExportResult.Success;
            }
        }

        private sealed class DurationExporter(ConcurrentQueue<Duration> exported)
            : BaseExporter<Metric>
        {
            public override ExportResult Export(in Batch<Metric> batch)
            {
                foreach (var metric in batch)
                {
                    if (metric.Name != OperationTelemetry.DurationName)
                    {
                        continue;
                    }

                    foreach (ref readonly var point in metric.GetMetricPoints())
                    {
                        Dictionary<string, string?> tags = [];
                        foreach (var tag in point.Tags)
                        {
                            tags[tag.Key] = tag.Value as string;
                        }

                        List<double> boundaries = [];
                        foreach (var bucket in point.GetHistogramBuckets())
                        {
                            // The last bucket's bound is positive infinity, which no boundary declares.
                            if (!double.IsPositiveInfinity(bucket.ExplicitBound))
                            {
                                boundaries.Add(bucket.ExplicitBound);
                            }
                        }

                        exported.Enqueue(
                            new Duration(
                                tags.GetValueOrDefault(OperationTelemetry.ModuleTag),
                                tags.GetValueOrDefault(OperationTelemetry.OperationTag),
                                tags.GetValueOrDefault(OperationTelemetry.OutcomeTag),
                                point.GetHistogramCount(),
                                point.GetHistogramSum(),
                                boundaries
                            )
                        );
                    }
                }

                return ExportResult.Success;
            }
        }
    }
}
