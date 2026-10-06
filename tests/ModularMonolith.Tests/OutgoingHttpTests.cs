using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using PouryaKit.Cqrs;
using PouryaKit.Messaging;
using PouryaKit.Testing;
using PouryaKit.Web;
using Polly;
using WireMock;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ModularMonolith.Tests;

// Outgoing HTTP against a WireMock.Net server, whose scenarios fail requests on cue and whose request journal shows
// what actually arrived. A client is composed as a Host composes one, with no Aspire or ServiceDefaults; the headers are
// read by PouryaKit.Messaging's names.
public sealed class OutgoingHttpTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private const string Client = "downstream";

    private static readonly OperationContext Context = new(Actor.Person("ada"), "acme");

    private readonly WireMockServer downstream = WireMockServer.Start();

    public override ValueTask DisposeAsync()
    {
        downstream.Stop();
        downstream.Dispose();
        return base.DisposeAsync();
    }

    [Fact]
    public async Task A_safe_request_is_retried_through_transient_failures_with_the_same_context_on_every_attempt()
    {
        RespondInTurn("GET", 503, 503, 200);
        using var services = Clients();

        using var response = await SendAsync(services, HttpMethod.Get);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var attempts = Journal();
        Assert.Equal(3, attempts.Count);
        Assert.All(
            attempts,
            attempt =>
                Assert.Equal(
                    new Dictionary<string, string>
                    {
                        [OperationContextHeaders.ActorId] = "ada",
                        [OperationContextHeaders.ActorKind] = nameof(ActorKind.Person),
                        [OperationContextHeaders.Tenant] = "acme",
                        [OperationContextHeaders.CorrelationId] = Context.CorrelationId.ToString(),
                        [OperationContextHeaders.CausationId] = Context.OperationId.ToString(),
                    },
                    ContextHeaders(attempt)
                )
        );
    }

    [Fact]
    public async Task Retries_stop_at_the_configured_limit()
    {
        RespondInTurn("GET", 503);
        using var services = Clients(maxRetryAttempts: 2);

        using var response = await SendAsync(services, HttpMethod.Get);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, Journal().Count);
    }

    [Fact]
    public async Task Cancelling_the_call_stops_further_attempts()
    {
        RespondInTurn("GET", 503);
        using var services = Clients(retryDelay: TimeSpan.FromSeconds(1));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SendAsync(services, HttpMethod.Get, cancellation.Token));
        // Long enough for the three retries a call that ignored the cancellation would still make.
        await Task.Delay(TimeSpan.FromSeconds(3.5), TestContext.Current.CancellationToken);

        Assert.Single(Journal());
    }

    [Fact]
    public async Task An_unsafe_write_is_sent_once_by_default_and_retried_only_by_a_replay_safe_client()
    {
        RespondInTurn("POST", 503, 200);
        using var safeMethods = Clients();
        using var replaySafe = Clients(HttpReplay.AllRequests);

        using var once = await SendAsync(safeMethods, HttpMethod.Post);
        downstream.ResetScenario("POST");
        downstream.ResetLogEntries();
        using var replayed = await SendAsync(replaySafe, HttpMethod.Post);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, once.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        Assert.Equal(2, Journal().Count);
    }

    // A write whose connection broke may still have taken effect, so it is the case that most needs no replay. WireMock
    // answers every request it reads, so a listener that resets each connection stands in for the downstream here.
    [Fact]
    public async Task A_write_whose_connection_broke_is_not_sent_again_while_a_read_is()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var connections = 0;
        // Runs until the listener is disposed with the test.
        _ = Task.Run(async () =>
        {
            while (true)
            {
                using var connection = await listener.AcceptSocketAsync(TestContext.Current.CancellationToken);
                Interlocked.Increment(ref connections);
                await connection.ReceiveAsync(new byte[4096], TestContext.Current.CancellationToken);
                connection.LingerState = new LingerOption(true, 0);
            }
        }, TestContext.Current.CancellationToken);
        using var services = Clients();
        var url = $"http://{listener.LocalEndpoint}/downstream";

        async Task<int> ConnectionsFor(HttpMethod method)
        {
            Volatile.Write(ref connections, 0);
            using var request = new HttpRequestMessage(method, url).WithOperationContext(Context);
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                services
                    .GetRequiredService<IHttpClientFactory>()
                    .CreateClient(Client)
                    .SendAsync(request, TestContext.Current.CancellationToken)
            );
            return Volatile.Read(ref connections);
        }

        Assert.Equal(1, await ConnectionsFor(HttpMethod.Post));
        Assert.Equal(4, await ConnectionsFor(HttpMethod.Get));
    }

    [Fact]
    public async Task A_request_without_an_operation_context_is_refused_before_it_is_sent()
    {
        RespondInTurn("GET", 200);
        using var services = Clients();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{downstream.Url}/downstream");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            services
                .GetRequiredService<IHttpClientFactory>()
                .CreateClient(Client)
                .SendAsync(request, TestContext.Current.CancellationToken)
        );

        Assert.Contains("WithOperationContext", refused.Message);
        Assert.Empty(Journal());
    }

    // Through the Host's own downstream client: each caller's request leaves with that caller's Operation Context and
    // without the Authorization header it arrived with.
    [Fact]
    public async Task An_endpoints_outgoing_call_carries_its_callers_context_and_never_the_callers_bearer_token()
    {
        RespondInTurn("GET", 200);
        using var ada = Fixture.Client("ada");
        using var bob = Fixture.Client("bob");
        var relay = $"/stub/relay?url={Uri.EscapeDataString($"{downstream.Url}/downstream")}";

        var sent = await Task.WhenAll(
            ada.GetFromJsonAsync<SeenContext>(relay, TestContext.Current.CancellationToken),
            bob.GetFromJsonAsync<SeenContext>(relay, TestContext.Current.CancellationToken)
        );

        var received = Journal();
        Assert.Equal(2, received.Count);
        Assert.All(received, request => Assert.DoesNotContain("authorization", Headers(request).Keys));
        Assert.Equal(
            sent.Select(context => (context!.Actor.Id, context.CorrelationId.ToString(), context.OperationId.ToString()))
                .Order(),
            received
                .Select(ContextHeaders)
                .Select(headers =>
                    (
                        headers[OperationContextHeaders.ActorId],
                        headers[OperationContextHeaders.CorrelationId],
                        headers[OperationContextHeaders.CausationId]
                    )
                )
                .Order()
        );
    }

    // The downstream answers each request with the next status in turn, then the last one for good.
    private void RespondInTurn(string method, params int[] statuses)
    {
        for (var turn = 0; turn < statuses.Length; turn++)
        {
            var mapping = downstream.Given(Request.Create().WithPath("/downstream").UsingMethod(method)).InScenario(method);
            if (turn > 0)
            {
                mapping = mapping.WhenStateIs($"answered {turn}");
            }

            if (turn < statuses.Length - 1)
            {
                mapping = mapping.WillSetStateTo($"answered {turn + 1}");
            }

            mapping.RespondWith(Response.Create().WithStatusCode(statuses[turn]));
        }
    }

    // A client composed as the sample Host composes its downstream client, at test speed.
    private static ServiceProvider Clients(
        HttpReplay replay = HttpReplay.SafeMethods,
        int maxRetryAttempts = 3,
        TimeSpan? retryDelay = null
    )
    {
        var services = new ServiceCollection();
        var client = services.AddHttpClient(Client);
        client
            .AddPouryaKitResilience(replay)
            .Configure(options =>
            {
                options.Retry.MaxRetryAttempts = maxRetryAttempts;
                options.Retry.Delay = retryDelay ?? TimeSpan.FromMilliseconds(10);
                options.Retry.BackoffType = DelayBackoffType.Constant;
                options.Retry.UseJitter = false;
            });
        client.AddPouryaKitOperationContext();
        return services.BuildServiceProvider();
    }

    private async Task<HttpResponseMessage> SendAsync(
        ServiceProvider services,
        HttpMethod method,
        CancellationToken? cancellationToken = null
    )
    {
        using var request = new HttpRequestMessage(method, $"{downstream.Url}/downstream").WithOperationContext(Context);
        return await services
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(Client)
            .SendAsync(request, cancellationToken ?? TestContext.Current.CancellationToken);
    }

    private List<IRequestMessage> Journal() => [.. downstream.LogEntries.Select(entry => entry.RequestMessage!)];

    private static Dictionary<string, string> Headers(IRequestMessage request) =>
        request.Headers!.ToDictionary(header => header.Key.ToLowerInvariant(), header => string.Join(",", header.Value));

    private static Dictionary<string, string> ContextHeaders(IRequestMessage request) =>
        Headers(request).Where(header => header.Key.StartsWith("pouryakit-", StringComparison.Ordinal)).ToDictionary();
}
