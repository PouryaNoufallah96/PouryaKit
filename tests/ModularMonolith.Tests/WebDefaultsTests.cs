using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using PouryaKit.Cqrs;
using PouryaKit.Messaging;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

// The Host's configured CORS policies, response compression and rate limit per Actor, each on a Host composed with its
// own configuration and served through the whole pipeline.
public sealed class WebDefaultsTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private const string AllowedOrigin = "https://app.example";

    [Fact]
    public async Task The_pipelines_named_policy_grants_a_preflight_and_a_request_from_its_configured_origin_and_nothing_to_another()
    {
        await using var host = Compose(
            ("Cors:Policies:browser:Origins:0", AllowedOrigin),
            ("Cors:Policies:browser:Methods:0", "GET"),
            ("Cors:Policies:browser:Methods:1", "POST"),
            ("Cors:Policies:browser:Headers:0", "Content-Type"),
            ("Cors:Policies:browser:ExposedHeaders:0", "Location"),
            ("Cors:Policies:browser:AllowCredentials", "true"),
            ("Cors:Policies:browser:PreflightMaxAge", "00:10:00")
        );
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var anonymous = AppFixture.ClientOf(host);

        var preflight = await anonymous.SendAsync(Preflight(AllowedOrigin), TestContext.Current.CancellationToken);
        var served = await anonymous.SendAsync(Get(AllowedOrigin), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, preflight.StatusCode);
        Assert.Equal(AllowedOrigin, Header(preflight, "Access-Control-Allow-Origin"));
        Assert.Equal("GET,POST", Header(preflight, "Access-Control-Allow-Methods"));
        Assert.Equal("Content-Type", Header(preflight, "Access-Control-Allow-Headers"), ignoreCase: true);
        Assert.Equal("true", Header(preflight, "Access-Control-Allow-Credentials"));
        Assert.Equal("600", Header(preflight, "Access-Control-Max-Age"));
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(AllowedOrigin, Header(served, "Access-Control-Allow-Origin"));
        Assert.Equal("Location", Header(served, "Access-Control-Expose-Headers"));
        // Another host or scheme is another origin: no permission header at all.
        foreach (var origin in new[] { "https://evil.example", "http://app.example" })
        {
            await AssertNoCorsPermissionAsync(anonymous, origin);
        }
    }

    [Fact]
    public async Task A_named_policy_without_configured_origins_or_without_any_configuration_grants_no_origin()
    {
        await using var host = Compose(("Cors:Policies:browser:Methods:0", "GET"));
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var withoutOrigins = AppFixture.ClientOf(host);
        using var unconfigured = Fixture.Client();

        await AssertNoCorsPermissionAsync(withoutOrigins, AllowedOrigin);
        await AssertNoCorsPermissionAsync(unconfigured, AllowedOrigin);
    }

    [Theory]
    [InlineData("Cors:Policies:browser:Origins:0", "*")]
    [InlineData("Cors:Policies:browser:Origins:0", "https://*.example")]
    [InlineData("Cors:Policies:browser:Origins:0", "https://app.example/")]
    [InlineData("Cors:Policies:browser:Origins:0", "null")]
    [InlineData("Cors:Policies:browser:Headers:0", "*")]
    [InlineData("RateLimit:PermitLimit", "0")]
    [InlineData("RateLimit:Window", "00:00:00")]
    [InlineData("RateLimit:QueueLimit", "-1")]
    public async Task A_host_whose_web_defaults_are_misconfigured_fails_when_it_starts(string key, string value)
    {
        Dictionary<string, string> rateLimit = new()
        {
            ["RateLimit:Enabled"] = "true",
            ["RateLimit:PermitLimit"] = "10",
            ["RateLimit:Window"] = "00:01:00",
            ["RateLimit:QueueLimit"] = "0",
        };
        rateLimit[key] = value;
        await using var host = Compose([.. rateLimit.Select(setting => (setting.Key, setting.Value))]);

        var thrown = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken)
        );

        Assert.Contains(key.StartsWith("Cors", StringComparison.Ordinal) ? $"'{value}'" : key.Split(':')[1], thrown.Message);
    }

    [Fact]
    public async Task Compression_when_enabled_answers_https_requests_with_negotiated_compressed_content()
    {
        await using var host = Compose(("Compression:Enabled", "true"));
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var https = AppFixture.ClientOf(host);
        https.BaseAddress = new Uri("https://localhost/");
        using var unconfigured = Fixture.Client();
        unconfigured.BaseAddress = new Uri("https://localhost/");

        var plain = await https.GetAsync("/catalog/products", TestContext.Current.CancellationToken);
        var compressed = await https.SendAsync(Gzip("/catalog/products"), TestContext.Current.CancellationToken);
        var uncompressed = await unconfigured.SendAsync(Gzip("/catalog/products"), TestContext.Current.CancellationToken);

        Assert.Empty(plain.Content.Headers.ContentEncoding);
        Assert.Equal("gzip", Assert.Single(compressed.Content.Headers.ContentEncoding));
        Assert.Contains("Accept-Encoding", compressed.Headers.Vary);
        await using var gzip = new GZipStream(
            await compressed.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
            CompressionMode.Decompress
        );
        using var decompressed = new StreamReader(gzip);
        Assert.Equal(
            await plain.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            await decompressed.ReadToEndAsync(TestContext.Current.CancellationToken)
        );
        // A Host that configures nothing compresses nothing.
        Assert.Equal(HttpStatusCode.OK, uncompressed.StatusCode);
        Assert.Empty(uncompressed.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task Each_actor_has_its_own_partition_which_spoofed_host_and_forwarded_for_headers_cannot_move_and_its_exhaustion_answers_429_as_a_problem_response()
    {
        await using var host = Compose(RateLimit(permitLimit: 1, window: "00:10:00", queueLimit: 0));
        host.MapGet("/limited", () => "served");
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var ada = AppFixture.ClientOf(host, "ada");
        using var bob = AppFixture.ClientOf(host, "bob");

        var first = await ada.GetAsync("/limited", TestContext.Current.CancellationToken);
        var spoofed = await ada.SendAsync(Spoofed("/limited", "203.0.113.7"), TestContext.Current.CancellationToken);
        var again = await ada.GetAsync("/limited", TestContext.Current.CancellationToken);
        var other = await bob.SendAsync(Spoofed("/limited", "203.0.113.7"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var problem = await ProblemAsync(spoofed, HttpStatusCode.TooManyRequests);
        Assert.Equal(429, problem.GetProperty("status").GetInt32());
        Assert.Equal("Too Many Requests", problem.GetProperty("title").GetString());
        Assert.False(problem.TryGetProperty("errors", out _));
        await ProblemAsync(again, HttpStatusCode.TooManyRequests);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task Anonymous_callers_share_the_partition_of_the_actor_their_endpoint_names_whatever_host_and_forwarded_for_headers_they_send()
    {
        await using var host = Compose(RateLimit(permitLimit: 1, window: "00:10:00", queueLimit: 0));
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var anonymous = AppFixture.ClientOf(host);
        using var ada = AppFixture.ClientOf(host, "ada");

        // The storefront's visitors run as catalog.storefront, the Stub's as stub.visitor.
        var first = await anonymous.SendAsync(Spoofed("/catalog/products", "198.51.100.1"), TestContext.Current.CancellationToken);
        var second = await anonymous.SendAsync(Spoofed("/catalog/products", "198.51.100.2"), TestContext.Current.CancellationToken);
        var otherActor = await anonymous.GetAsync("/stub/public/context", TestContext.Current.CancellationToken);
        var person = await ada.GetAsync("/catalog/products", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await ProblemAsync(second, HttpStatusCode.TooManyRequests);
        Assert.Equal(HttpStatusCode.OK, otherActor.StatusCode);
        Assert.Equal(HttpStatusCode.OK, person.StatusCode);
    }

    [Fact]
    public async Task A_trusted_peers_call_shares_the_partition_of_the_actor_it_continues_and_an_untrusted_callers_context_headers_move_nothing()
    {
        await using var host = Compose(RateLimit(permitLimit: 1, window: "00:10:00", queueLimit: 0));
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var ada = AppFixture.ClientOf(host, "ada");
        using var peer = AppFixture.ClientOf(host, AppFixture.TrustedPeer);
        using var mallory = AppFixture.ClientOf(host, "mallory");

        var direct = await ada.GetAsync("/stub/context", TestContext.Current.CancellationToken);
        var continuingAda = await peer.SendAsync(Continuing("ada"), TestContext.Current.CancellationToken);
        var continuingBob = await peer.SendAsync(Continuing("bob"), TestContext.Current.CancellationToken);
        var untrusted = await mallory.GetAsync("/stub/context", TestContext.Current.CancellationToken);
        var untrustedWithHeaders = await mallory.SendAsync(Continuing("carol"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, direct.StatusCode);
        await ProblemAsync(continuingAda, HttpStatusCode.TooManyRequests);
        Assert.Equal(HttpStatusCode.OK, continuingBob.StatusCode);
        Assert.Equal(HttpStatusCode.OK, untrusted.StatusCode);
        // An untrusted caller's context headers are ignored: it stays in its own, exhausted partition.
        await ProblemAsync(untrustedWithHeaders, HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_request_beyond_the_limit_waits_in_a_queue_of_its_own_size_until_the_next_window_and_one_beyond_the_queue_answers_429()
    {
        // Two permits and a queue of one: the queue's size is not the permit limit.
        await using var host = Compose(RateLimit(permitLimit: 2, window: "00:00:03", queueLimit: 1));
        host.MapGet("/limited", () => "served");
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var ada = AppFixture.ClientOf(host, "ada");
        Assert.Equal(HttpStatusCode.OK, (await ada.GetAsync("/limited", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ada.GetAsync("/limited", TestContext.Current.CancellationToken)).StatusCode);

        Task<HttpResponseMessage>[] beyond =
        [
            ada.GetAsync("/limited", TestContext.Current.CancellationToken),
            ada.GetAsync("/limited", TestContext.Current.CancellationToken),
        ];
        var answered = await Task.WhenAny(beyond);
        var waiting = answered == beyond[0] ? beyond[1] : beyond[0];

        // One is refused at once, while the other still waits in the queue; it is served when the window ends.
        Assert.False(waiting.IsCompleted);
        await ProblemAsync(await answered, HttpStatusCode.TooManyRequests);
        Assert.Equal(HttpStatusCode.OK, (await waiting).StatusCode);
    }

    private WebApplication Compose(params (string Key, string Value)[] settings) =>
        AppFixture.ComposeHost(
            "Production",
            Fixture.ConnectionString,
            new RecordedLogs(),
            builder =>
                builder.Configuration.AddInMemoryCollection(
                    settings.Select(setting => KeyValuePair.Create($"PouryaKit:Web:{setting.Key}", (string?)setting.Value))
                )
        );

    private static (string, string)[] RateLimit(int permitLimit, string window, int queueLimit) =>
        [
            ("RateLimit:Enabled", "true"),
            ("RateLimit:PermitLimit", $"{permitLimit}"),
            ("RateLimit:Window", window),
            ("RateLimit:QueueLimit", $"{queueLimit}"),
        ];

    // A browser's preflight before a cross-origin POST of JSON to the storefront's products.
    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/catalog/products");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");
        return request;
    }

    private static HttpRequestMessage Get(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/catalog/products");
        request.Headers.Add("Origin", origin);
        return request;
    }

    private static HttpRequestMessage Gzip(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.AcceptEncoding.ParseAdd("gzip");
        return request;
    }

    // A call that continues an Operation of the person actorId, as a peer Host sends it.
    private static HttpRequestMessage Continuing(string actorId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/stub/context");
        request.Headers.Add(OperationContextHeaders.ActorId, actorId);
        request.Headers.Add(OperationContextHeaders.ActorKind, nameof(ActorKind.Person));
        request.Headers.Add(OperationContextHeaders.CorrelationId, Guid.CreateVersion7().ToString());
        request.Headers.Add(OperationContextHeaders.CausationId, Guid.CreateVersion7().ToString());
        return request;
    }

    // A caller that claims another Host and another client address.
    private static HttpRequestMessage Spoofed(string path, string forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = $"spoofed-{forwardedFor}.example";
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return request;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(',', values) : null;

    private static async Task AssertNoCorsPermissionAsync(HttpClient client, string origin)
    {
        var preflight = await client.SendAsync(Preflight(origin), TestContext.Current.CancellationToken);
        var request = await client.SendAsync(Get(origin), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(preflight.Headers, header => header.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.OK, request.StatusCode);
        Assert.DoesNotContain(request.Headers, header => header.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return problem.RootElement.Clone();
    }
}
