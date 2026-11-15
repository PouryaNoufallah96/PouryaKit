using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Messaging;
using PouryaKit.Testing;
using static ModularMonolith.Tests.Fixtures.ProblemResponses;

namespace ModularMonolith.Tests;

// The Host's configured CORS policies, response compression and rate limit per Actor, each on a Host composed with its
// own configuration and served through the whole pipeline, and ASP.NET Core's CSRF protection on the shared Host's.
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

        var preflight = await anonymous.SendAsync(
            Preflight(AllowedOrigin),
            TestContext.Current.CancellationToken
        );
        var served = await anonymous.SendAsync(
            Get(AllowedOrigin),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.NoContent, preflight.StatusCode);
        Assert.Equal(AllowedOrigin, Header(preflight, "Access-Control-Allow-Origin"));
        Assert.Equal("GET,POST", Header(preflight, "Access-Control-Allow-Methods"));
        Assert.Equal(
            "Content-Type",
            Header(preflight, "Access-Control-Allow-Headers"),
            ignoreCase: true
        );
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
    public async Task A_host_whose_web_defaults_are_misconfigured_fails_when_it_starts(
        string key,
        string value
    )
    {
        Dictionary<string, string> rateLimit = new()
        {
            ["RateLimit:Enabled"] = "true",
            ["RateLimit:PermitLimit"] = "10",
            ["RateLimit:Window"] = "00:01:00",
            ["RateLimit:QueueLimit"] = "0",
        };
        rateLimit[key] = value;
        await using var host = Compose([
            .. rateLimit.Select(setting => (setting.Key, setting.Value)),
        ]);

        var thrown = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken)
        );

        Assert.Contains(
            key.StartsWith("Cors", StringComparison.Ordinal) ? $"'{value}'" : key.Split(':')[1],
            thrown.Message
        );
    }

    [Fact]
    public async Task Compression_when_enabled_answers_https_requests_with_negotiated_compressed_content()
    {
        await using var host = Compose(("Compression:Enabled", "true"));
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var https = AppFixture.ClientOf(host, "ada", AppFixture.TestTenant);
        https.BaseAddress = new Uri("https://localhost/");
        using var unconfigured = Fixture.Client("ada", AppFixture.TestTenant);
        unconfigured.BaseAddress = new Uri("https://localhost/");

        var plain = await https.GetAsync(
            "/catalog/products",
            TestContext.Current.CancellationToken
        );
        var compressed = await https.SendAsync(
            Compressed("/catalog/products", "gzip"),
            TestContext.Current.CancellationToken
        );
        var zstd = await https.SendAsync(
            Compressed("/catalog/products", "zstd"),
            TestContext.Current.CancellationToken
        );
        var uncompressed = await unconfigured.SendAsync(
            Compressed("/catalog/products", "gzip"),
            TestContext.Current.CancellationToken
        );

        Assert.Empty(plain.Content.Headers.ContentEncoding);
        // The response a cache must not reuse for a caller that asks another encoding, compressed or not.
        Assert.Contains("Accept-Encoding", plain.Headers.Vary);
        var body = await plain.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("gzip", Assert.Single(compressed.Content.Headers.ContentEncoding));
        Assert.Contains("Accept-Encoding", compressed.Headers.Vary);
        await using (
            var gzip = new GZipStream(
                await compressed.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
                CompressionMode.Decompress
            )
        )
        {
            Assert.Equal(body, await ReadToEndAsync(gzip));
        }

        Assert.Equal("zstd", Assert.Single(zstd.Content.Headers.ContentEncoding));
        await using (
            var zstandard = new ZstandardStream(
                await zstd.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
                CompressionMode.Decompress
            )
        )
        {
            Assert.Equal(body, await ReadToEndAsync(zstandard));
        }

        // A Host that configures nothing compresses nothing.
        Assert.Equal(HttpStatusCode.OK, uncompressed.StatusCode);
        Assert.Empty(uncompressed.Content.Headers.ContentEncoding);
    }

    // A browser on another site posts JSON with its fetch metadata: ASP.NET Core's CSRF protection marks it, and only an
    // endpoint that reads a form enforces that mark, so a JSON endpoint serves it as any other request.
    [Fact]
    public async Task A_cross_site_post_of_json_is_served()
    {
        using var ada = Fixture.Client("ada", AppFixture.TestTenant);
        var category = Guid.CreateVersion7();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/catalog/categories")
        {
            Content = JsonContent.Create(new { categoryId = category, name = "Cross-site" }),
        };
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");

        var created = await ada.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(
            category,
            await created.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task Each_actor_has_its_own_partition_which_spoofed_host_and_forwarded_for_headers_cannot_move_and_its_exhaustion_answers_429_as_a_problem_response()
    {
        await using var host = Compose(
            RateLimit(permitLimit: 1, window: "00:10:00", queueLimit: 0)
        );
        host.MapGet("/limited", () => "served");
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var ada = AppFixture.ClientOf(host, "ada");
        using var bob = AppFixture.ClientOf(host, "bob");

        var first = await ada.GetAsync("/limited", TestContext.Current.CancellationToken);
        var spoofed = await ada.SendAsync(
            Spoofed("/limited", "203.0.113.7"),
            TestContext.Current.CancellationToken
        );
        var again = await ada.GetAsync("/limited", TestContext.Current.CancellationToken);
        var other = await bob.SendAsync(
            Spoofed("/limited", "203.0.113.7"),
            TestContext.Current.CancellationToken
        );

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
        await using var host = Compose(
            RateLimit(permitLimit: 1, window: "00:10:00", queueLimit: 0)
        );
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var anonymous = AppFixture.ClientOf(host);
        using var ada = AppFixture.ClientOf(host, "ada");

        // The public endpoint's visitors run as stub.visitor, the kiosk's as stub.kiosk.
        var first = await anonymous.SendAsync(
            Spoofed("/stub/public/context", "198.51.100.1"),
            TestContext.Current.CancellationToken
        );
        var second = await anonymous.SendAsync(
            Spoofed("/stub/public/context", "198.51.100.2"),
            TestContext.Current.CancellationToken
        );
        var otherActor = await anonymous.GetAsync(
            "/stub/kiosk/context",
            TestContext.Current.CancellationToken
        );
        var person = await ada.GetAsync(
            "/stub/public/context",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await ProblemAsync(second, HttpStatusCode.TooManyRequests);
        Assert.Equal(HttpStatusCode.OK, otherActor.StatusCode);
        Assert.Equal(HttpStatusCode.OK, person.StatusCode);
    }

    [Fact]
    public async Task A_trusted_peers_call_shares_the_partition_of_the_actor_it_continues_and_an_untrusted_callers_context_headers_move_nothing()
    {
        await using var host = Compose(
            RateLimit(permitLimit: 1, window: "00:10:00", queueLimit: 0)
        );
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var ada = AppFixture.ClientOf(host, "ada");
        using var peer = AppFixture.ClientOf(host, AppFixture.TrustedPeer);
        using var mallory = AppFixture.ClientOf(host, "mallory");

        var direct = await ada.GetAsync("/stub/context", TestContext.Current.CancellationToken);
        var continuingAda = await peer.SendAsync(
            Continuing("ada"),
            TestContext.Current.CancellationToken
        );
        var continuingBob = await peer.SendAsync(
            Continuing("bob"),
            TestContext.Current.CancellationToken
        );
        var untrusted = await mallory.GetAsync(
            "/stub/context",
            TestContext.Current.CancellationToken
        );
        var untrustedWithHeaders = await mallory.SendAsync(
            Continuing("carol"),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, direct.StatusCode);
        await ProblemAsync(continuingAda, HttpStatusCode.TooManyRequests);
        Assert.Equal(HttpStatusCode.OK, continuingBob.StatusCode);
        Assert.Equal(HttpStatusCode.OK, untrusted.StatusCode);
        // An untrusted caller's context headers are ignored: it stays in its own, exhausted partition.
        await ProblemAsync(untrustedWithHeaders, HttpStatusCode.TooManyRequests);
    }

    // The partition names the Actor's kind as well as its id: a person who bears the public endpoint's name is not one of
    // its anonymous visitors, who run as the system Actor stub.visitor.
    [Fact]
    public async Task A_person_named_as_a_public_endpoints_actor_has_a_partition_apart_from_its_anonymous_callers()
    {
        await using var host = Compose(
            RateLimit(permitLimit: 1, window: "00:10:00", queueLimit: 0)
        );
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var anonymous = AppFixture.ClientOf(host);
        using var namesake = AppFixture.ClientOf(host, ReadOperationContextEndpoint.Visitor.Id);

        var visitor = await anonymous.GetAsync(
            "/stub/public/context",
            TestContext.Current.CancellationToken
        );
        var person = await namesake.GetAsync(
            "/stub/public/context",
            TestContext.Current.CancellationToken
        );
        var anotherVisitor = await anonymous.GetAsync(
            "/stub/public/context",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, visitor.StatusCode);
        Assert.Equal(HttpStatusCode.OK, person.StatusCode);
        await ProblemAsync(anotherVisitor, HttpStatusCode.TooManyRequests);
    }

    // Headers no Operation Context can be read from resolve no Actor, so the limiter counts the call in the one partition
    // of requests no Actor resolves for, and the endpoint, not the limiter, refuses it.
    [Fact]
    public async Task A_trusted_peers_unreadable_context_headers_count_in_the_partition_no_actor_resolves_for_and_answer_400()
    {
        await using var host = Compose(
            RateLimit(permitLimit: 1, window: "00:10:00", queueLimit: 0)
        );
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var peer = AppFixture.ClientOf(host, AppFixture.TrustedPeer);

        var unreadable = await peer.SendAsync(
            Continuing("ada", correlationId: "not-a-guid"),
            TestContext.Current.CancellationToken
        );
        var unreadableAgain = await peer.SendAsync(
            Continuing("ada", correlationId: "not-a-guid"),
            TestContext.Current.CancellationToken
        );
        var continuingAda = await peer.SendAsync(
            Continuing("ada"),
            TestContext.Current.CancellationToken
        );
        var direct = await peer.GetAsync("/stub/context", TestContext.Current.CancellationToken);

        await ProblemAsync(unreadable, HttpStatusCode.BadRequest);
        await ProblemAsync(unreadableAgain, HttpStatusCode.TooManyRequests);
        Assert.Equal(HttpStatusCode.OK, continuingAda.StatusCode);
        Assert.Equal(HttpStatusCode.OK, direct.StatusCode);
    }

    [Fact]
    public async Task A_request_beyond_the_limit_waits_in_a_queue_of_its_own_size_until_the_next_window_and_one_beyond_the_queue_answers_429()
    {
        // Two permits and a queue of one: the queue's size is not the permit limit.
        await using var host = Compose(
            RateLimit(permitLimit: 2, window: "00:00:03", queueLimit: 1)
        );
        host.MapGet("/limited", () => "served");
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var ada = AppFixture.ClientOf(host, "ada");
        Assert.Equal(
            HttpStatusCode.OK,
            (await ada.GetAsync("/limited", TestContext.Current.CancellationToken)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.OK,
            (await ada.GetAsync("/limited", TestContext.Current.CancellationToken)).StatusCode
        );

        Task<HttpResponseMessage>[] beyond =
        [
            ada.GetAsync("/limited", TestContext.Current.CancellationToken),
            ada.GetAsync("/limited", TestContext.Current.CancellationToken),
        ];
        var answered = await Task.WhenAny(beyond);
        var waiting = answered == beyond[0] ? beyond[1] : beyond[0];

        // One is refused at once, while the other still waits in the queue; it is served when the window ends.
        Assert.False(waiting.IsCompleted);
        var refused = await answered;
        await ProblemAsync(refused, HttpStatusCode.TooManyRequests);
        Assert.InRange(RetryAfterSeconds(refused), 1, 3);
        Assert.Equal(HttpStatusCode.OK, (await waiting).StatusCode);
    }

    // A refusal names the seconds left in its window as the limiter counts them, not the window's length, rounded up, so a
    // caller that waits that long is not refused again a fraction of a second early.
    [Fact]
    public async Task A_refusal_tells_the_caller_in_retry_after_the_seconds_left_in_its_window()
    {
        await using var host = Compose(
            RateLimit(permitLimit: 1, window: "00:10:00", queueLimit: 0)
        );
        host.MapGet("/limited", () => "served");
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var ada = AppFixture.ClientOf(host, "ada");

        // Started before the first request creates ada's partition and its window, stopped after the refusal is read: it
        // spans at least the limiter's own count, so the time it leaves of the 600 is never more than the time left.
        var spanned = Stopwatch.StartNew();
        var first = await ada.GetAsync("/limited", TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);
        var refused = await ada.GetAsync("/limited", TestContext.Current.CancellationToken);
        spanned.Stop();

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.False(first.Headers.Contains("Retry-After"));
        await ProblemAsync(refused, HttpStatusCode.TooManyRequests);
        // Rounded up, never below the time left; and at least the second and a half waited is gone from the 600.
        Assert.InRange(RetryAfterSeconds(refused), 600 - spanned.Elapsed.TotalSeconds, 599);
    }

    // An application that set its own rejection handler before AddPouryaKitWeb keeps it beside the Kit's Retry-After.
    [Fact]
    public async Task A_rejection_handler_the_application_set_first_still_runs_beside_retry_after()
    {
        await using var host = Compose(
            builder =>
                builder.Services.Insert(
                    0,
                    ServiceDescriptor.Singleton<IConfigureOptions<RateLimiterOptions>>(
                        new ConfigureOptions<RateLimiterOptions>(limiter =>
                            limiter.OnRejected = (context, _) =>
                            {
                                context.HttpContext.Response.Headers["X-Rejected-By"] =
                                    "application";
                                return ValueTask.CompletedTask;
                            }
                        )
                    )
                ),
            RateLimit(permitLimit: 1, window: "00:10:00", queueLimit: 0)
        );
        host.MapGet("/limited", () => "served");
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var ada = AppFixture.ClientOf(host, "ada");

        await ada.GetAsync("/limited", TestContext.Current.CancellationToken);
        var refused = await ada.GetAsync("/limited", TestContext.Current.CancellationToken);

        await ProblemAsync(refused, HttpStatusCode.TooManyRequests);
        Assert.Equal("application", Header(refused, "X-Rejected-By"));
        Assert.InRange(RetryAfterSeconds(refused), 1, 600);
    }

    // The limiter partitions the request by the Actor of the Operation Context the endpoint then begins its Operation
    // with: one resolution serves both, so the caller's Actor claim is read once.
    [Fact]
    public async Task With_the_rate_limit_on_the_limiter_and_the_endpoint_share_one_resolution_of_the_callers_context()
    {
        ActorClaimReads reads = new();
        await using var host = Compose(
            builder => builder.Services.AddSingleton<IClaimsTransformation>(reads),
            RateLimit(permitLimit: 10, window: "00:10:00", queueLimit: 0)
        );
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var ada = AppFixture.ClientOf(host, "ada");

        var context = await ada.GetFromJsonAsync<SeenContext>(
            "/stub/context",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(("ada", 1), (context!.Actor.Id, reads.Count));
    }

    private WebApplication Compose(params (string Key, string Value)[] settings) =>
        Compose(_ => { }, settings);

    private WebApplication Compose(
        Action<WebApplicationBuilder> configure,
        params (string Key, string Value)[] settings
    ) =>
        AppFixture.ComposeHost(
            "Production",
            Fixture.ConnectionString,
            new RecordedLogs(),
            builder =>
            {
                builder.Configuration.AddInMemoryCollection(
                    settings.Select(setting =>
                        KeyValuePair.Create($"PouryaKit:Web:{setting.Key}", (string?)setting.Value)
                    )
                );
                configure(builder);
            }
        );

    private static (string, string)[] RateLimit(int permitLimit, string window, int queueLimit) =>
        [
            ("RateLimit:Enabled", "true"),
            ("RateLimit:PermitLimit", $"{permitLimit}"),
            ("RateLimit:Window", window),
            ("RateLimit:QueueLimit", $"{queueLimit}"),
        ];

    // A browser's preflight before a cross-origin POST of JSON to Catalog's products.
    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/catalog/products");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");
        return request;
    }

    // A cross-origin GET an anonymous caller is served.
    private static HttpRequestMessage Get(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/stub/public/context");
        request.Headers.Add("Origin", origin);
        return request;
    }

    private static HttpRequestMessage Compressed(string path, string encoding)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.AcceptEncoding.ParseAdd(encoding);
        return request;
    }

    private static async Task<string> ReadToEndAsync(Stream decompressing)
    {
        using var reader = new StreamReader(decompressing);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    // A call that continues an Operation of the person actorId, as a peer Host sends it, with correlationId when given.
    private static HttpRequestMessage Continuing(string actorId, string? correlationId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/stub/context");
        request.Headers.Add(OperationContextHeaders.ActorId, actorId);
        request.Headers.Add(OperationContextHeaders.ActorKind, nameof(ActorKind.Person));
        request.Headers.Add(
            OperationContextHeaders.CorrelationId,
            correlationId ?? Guid.CreateVersion7().ToString()
        );
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

    // Retry-After as a whole number of seconds, the form the Kit writes.
    private static double RetryAfterSeconds(HttpResponseMessage response) =>
        int.Parse(
            Assert.Single(response.Headers.GetValues("Retry-After")),
            CultureInfo.InvariantCulture
        );

    private static async Task AssertNoCorsPermissionAsync(HttpClient client, string origin)
    {
        var preflight = await client.SendAsync(
            Preflight(origin),
            TestContext.Current.CancellationToken
        );
        var request = await client.SendAsync(Get(origin), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(preflight.Headers, header =>
            header.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Equal(HttpStatusCode.OK, request.StatusCode);
        Assert.DoesNotContain(request.Headers, header =>
            header.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase)
        );
    }

    // Hands out each authenticated caller as a principal that counts the reads of its Actor claim, the claim the Kit
    // resolves the caller's Operation Context from. One request reads it in turn, never at once.
    private sealed class ActorClaimReads : IClaimsTransformation
    {
        public int Count { get; private set; }

        public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal) =>
            Task.FromResult<ClaimsPrincipal>(new Counted(principal, this));

        private sealed class Counted(ClaimsPrincipal principal, ActorClaimReads reads)
            : ClaimsPrincipal(principal)
        {
            public override Claim? FindFirst(string type)
            {
                if (type == ClaimTypes.NameIdentifier)
                {
                    reads.Count++;
                }

                return base.FindFirst(type);
            }
        }
    }
}
