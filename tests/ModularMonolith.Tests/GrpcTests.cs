using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Contracts.Products;
using Catalog.Products.Errors;
using Catalog.Products.Features.CreateProduct;
using Catalog.Products.Features.GetProduct;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Grpc;
using PouryaKit.Messaging;
using PouryaKit.Testing;
using PouryaKit.Web;

namespace ModularMonolith.Tests;

// Catalog's gRPC service on Web's conventions, through the Host's real pipeline: two Hosts of the application in this
// process, each with a client of the other's ProductService, and plain channels standing in for callers of any kind.
public sealed class GrpcTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private const string Client = "catalog";

    private const string Rule = "1 to 256 visible ASCII characters, with spaces only between them";

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Two_hosts_call_each_others_catalog_service_and_each_call_continues_the_callers_operation_as_a_trusted_peer()
    {
        var product = await CreateProductAsync();
        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);
        WebApplication? first = null;
        WebApplication? second = null;
        List<HttpRequestMessage> fromFirst = [];
        List<HttpRequestMessage> fromSecond = [];
        await using (first = ComposeHost(() => second!, fromFirst))
        await using (second = ComposeHost(() => first!, fromSecond))
        {
            await first.StartAsync(TestContext.Current.CancellationToken);
            await second.StartAsync(TestContext.Current.CancellationToken);
            var ada = new OperationContext(Actor.Person("ada"), "acme");
            var grace = new OperationContext(Actor.Person("grace"), "acme");

            var toSecond = await GetProductAsync(first, product, ada);
            var toFirst = await GetProductAsync(second, product, grace);
            var unsent = await Assert.ThrowsAsync<InvalidOperationException>(() => GetProductAsync(first, product, null));
            // The service's Module also maps prefixed HTTP endpoints, which answer beside it.
            using var person = AppFixture.ClientOf(second, "grace", "acme");
            var http = await person.GetAsync($"/catalog/products/{product}", TestContext.Current.CancellationToken);
            var seenBySecond = second.Services.GetRequiredService<CatalogQuestionContexts>().Seen;
            var seenByFirst = first.Services.GetRequiredService<CatalogQuestionContexts>().Seen;
            await second.StopAsync(TestContext.Current.CancellationToken);
            await first.StopAsync(TestContext.Current.CancellationToken);

            Assert.Equal((product.ToString(), Name(product)), (toSecond.Id, toSecond.Name));
            Assert.Equal((product.ToString(), Name(product)), (toFirst.Id, toFirst.Name));
            AssertContinued(ada, seenBySecond[0]);
            AssertContinued(grace, seenByFirst[0]);
            Assert.Contains("has no Operation Context", unsent.Message, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.OK, http.StatusCode);
            // Every call carries the client's deadline, and no bearer token: only the test's own peer credentials.
            Assert.All(
                [.. fromFirst, .. fromSecond],
                sent =>
                {
                    Assert.InRange(Timeout(sent), Deadline - TimeSpan.FromSeconds(5), Deadline);
                    Assert.Equal($"{TestAuthentication.SchemeName} {AppFixture.TrustedPeer}", sent.Headers.Authorization?.ToString());
                }
            );
            Assert.Equal(2, fromFirst.Count + fromSecond.Count);
        }
    }

    // The same Kit headers from a caller the Host does not trust are ignored: Grace runs as herself with no tenant, which
    // Catalog's tenant requirement refuses, where a continued Ada in acme would have found the product.
    [Fact]
    public async Task An_untrusted_callers_kit_headers_are_ignored()
    {
        var product = await CreateProductAsync();
        using var grace = Channel();

        var ignored = await FailureAsync(grace, product.ToString(), Caller("grace", ContextHeaders()));

        Assert.Equal(StatusCode.PermissionDenied, ignored.StatusCode);
        Assert.Equal([TenantErrors.Required.Code], Errors(ignored).Select(error => error.GetProperty("code").GetString()));
    }

    // The services' group requires an authenticated caller itself, also in a Host whose fallback policy admits anyone.
    [Fact]
    public async Task An_anonymous_call_is_unauthenticated_where_the_host_sets_no_fallback_policy()
    {
        await using var host = AppFixture.ComposeHost(
            "Development",
            Fixture.ConnectionString,
            Fixture.Logs,
            builder => builder.Services.PostConfigure<AuthorizationOptions>(authorization => authorization.FallbackPolicy = null)
        );
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var anonymous = Channel(host);

        var unauthenticated = await FailureAsync(anonymous, Guid.CreateVersion7().ToString(), ContextHeaders());
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(StatusCode.Unauthenticated, unauthenticated.StatusCode);
    }

    // The Kit's refusal of a trusted peer's unreadable headers is the status, not an HTTP 400 the client cannot read.
    [Theory]
    [InlineData(OperationContextHeaders.CausationId, "not-a-guid", $"The request's {OperationContextHeaders.CausationId} header is not a Guid.")]
    [InlineData(OperationContextHeaders.ActorKind, "Robot", $"The request's {OperationContextHeaders.ActorKind} header is Person or System.")]
    [InlineData(OperationContextHeaders.Tenant, "acmé", $"The request continues an Operation but has no single {OperationContextHeaders.Tenant} header that is {Rule}.")]
    public async Task A_trusted_peers_unreadable_context_headers_answer_invalid_argument_with_the_rule(string header, string value, string message)
    {
        using var peer = Channel();
        var headers = Caller(AppFixture.TrustedPeer, ContextHeaders());
        headers.Remove(headers.Get(header)!);
        headers.Add(header, value);

        var refused = await FailureAsync(peer, Guid.CreateVersion7().ToString(), headers);

        Assert.Equal((StatusCode.InvalidArgument, message), (refused.StatusCode, refused.Status.Detail));
    }

    [Fact]
    public async Task A_failed_result_answers_its_errors_kind_with_every_error_in_the_trailer()
    {
        using var channel = Channel();
        var person = Caller("grace", new Metadata { { TestAuthentication.TenantHeader, AppFixture.TestTenant } });

        var missing = await FailureAsync(channel, Guid.CreateVersion7().ToString(), person);
        var empty = await FailureAsync(channel, Guid.Empty.ToString(), person);

        Assert.Equal((StatusCode.NotFound, ProductErrors.NotFound.Message), (missing.StatusCode, missing.Status.Detail));
        Assert.Equal(
            [(ProductErrors.NotFound.Code, ProductErrors.NotFound.Message, (string?)null)],
            Errors(missing).Select(error => (error.GetProperty("code").GetString(), error.GetProperty("message").GetString(), Target(error)))
        );
        Assert.Equal(StatusCode.InvalidArgument, empty.StatusCode);
        Assert.Equal("ProductId", Target(Assert.Single(Errors(empty))));
    }

    // ADR 0008's gRPC amendment: the status follows the kind HTTP's status follows, for one kind and for every pair, and
    // the trailer lists every Error in order.
    [Fact]
    public void The_most_severe_error_kind_decides_the_status_as_it_decides_the_http_status()
    {
        Dictionary<int, StatusCode> statuses = new()
        {
            [400] = StatusCode.InvalidArgument,
            [404] = StatusCode.NotFound,
            [409] = StatusCode.Aborted,
            [403] = StatusCode.PermissionDenied,
            [500] = StatusCode.Internal,
        };
        Error[] kinds =
        [
            Error.Validation("Kind.Validation", "validation", "Name"),
            Error.NotFound("Kind.NotFound", "not found"),
            Error.Conflict("Kind.Conflict", "conflict"),
            Error.Forbidden("Kind.Forbidden", "forbidden"),
            Error.Unexpected("Kind.Unexpected", "unexpected"),
        ];

        foreach (ImmutableArray<Error> errors in kinds.Select(one => ImmutableArray.Create(one)).Concat(from a in kinds from b in kinds where a != b select ImmutableArray.Create(a, b)))
        {
            Result failed = errors;
            var http = Assert.IsType<ProblemHttpResult>(failed.ToHttpResult());
            var rpc = Assert.Throws<RpcException>(() => failed.ToGrpcResponse(new ProductReply()));

            Assert.Equal(statuses[http.StatusCode], rpc.StatusCode);
            Assert.Equal(errors.Select(error => error.Code), Errors(rpc).Select(error => error.GetProperty("code").GetString()));
        }
    }

    [Theory]
    [InlineData("--urls=http://localhost:5100", "http://127.0.0.1:5100", "http://localhost:5100")]
    [InlineData("--http_ports=5101", "http://localhost:5101/", "http://*:5101")]
    [InlineData("--Kestrel:Endpoints:Grpc:Url=https://catalog.local:5102", "https://CATALOG.local:5102", "https://catalog.local:5102")]
    // Review F6: Kestrel binds a listener's host name that is not localhost to every address.
    [InlineData("--Kestrel:Endpoints:Grpc:Url=http://catalog.local:5102", "http://127.0.0.1:5102", "http://catalog.local:5102")]
    // Review F5: preferHostingUrls keeps the urls over the Kestrel endpoints.
    [InlineData("--urls=http://localhost:9000 --Kestrel:Endpoints:Grpc:Url=http://localhost:9100 --preferHostingUrls=true", "http://localhost:9000", "http://localhost:9000")]
    public async Task A_client_addressed_at_one_of_its_own_hosts_listening_urls_fails_the_start(string listening, string address, string own)
    {
        await using var host = AppFixture.ComposeHost(
            "Development",
            Fixture.ConnectionString,
            new RecordedLogs(),
            builder => AddClient(builder.Services, new Uri(address)),
            listening.Split(' ')
        );

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains($"The gRPC client {Client} is addressed at {new Uri(address)}, one of this Host's own listening URLs ({own}).", refused.Message, StringComparison.Ordinal);
    }

    // Ticket #159: Kestrel keeps a listener set in code to itself until it binds, so the check compares what it bound.
    [Fact]
    public async Task A_client_addressed_at_a_listener_kestrel_binds_from_code_stops_the_host_and_fails_its_start()
    {
        var port = FreePort();
        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);
        await using var host = AppFixture.ComposeHost(
            "Development",
            Fixture.ConnectionString,
            new RecordedLogs(),
            builder =>
            {
                builder.WebHost.UseKestrel().ConfigureKestrel(kestrel => kestrel.ListenLocalhost(port));
                AddClient(builder.Services, new Uri($"http://localhost:{port}"));
            }
        );

        // Caught, as a caller that goes on would: the Host it leaves behind has stopped, and nothing listens on the port.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        using var probe = new TcpClient();
        var connecting = await Record.ExceptionAsync(() => probe.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(
            $"The gRPC client {Client} is addressed at http://localhost:{port}/, one of this Host's own listening URLs (http://localhost:{port}). A call between Modules of one Host goes through the Gateway, never a loopback; address the client at another Host. The server had already bound it, so the Host listened, and stopped, before its start failed.",
            refused.Message
        );
        Assert.True(host.Lifetime.ApplicationStopped.IsCancellationRequested);
        Assert.IsType<SocketException>(connecting);
    }

    // Ticket #159: Kestrel binds no default beside a listener set in code, so a client at the default calls another process.
    [Fact]
    public async Task A_client_addressed_at_kestrels_default_url_starts_beside_a_listener_kestrel_binds_from_code()
    {
        var port = FreePort();
        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);
        await using var host = AppFixture.ComposeHost(
            "Development",
            Fixture.ConnectionString,
            new RecordedLogs(),
            builder =>
            {
                builder.WebHost.UseKestrel().ConfigureKestrel(kestrel => kestrel.ListenLocalhost(port));
                AddClient(builder.Services, new Uri("http://localhost:5000"));
            }
        );

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    // Kestrel's default, as Kestrel lists it once bound, compared without binding its port, which the machine may hold.
    [Theory]
    [InlineData("http://localhost:5000", true)]
    [InlineData("http://127.0.0.1:5000", true)]
    [InlineData("http://localhost:5001", false)]
    public void Kestrels_default_among_the_bound_addresses_refuses_a_client_there_and_no_other(string address, bool refused)
    {
        var services = new ServiceCollection();
        AddClient(services, new Uri(address));
        using var provider = services.BuildServiceProvider();

        var refusals = GrpcLoopbackCheck.Refusals(["http://localhost:5000"], provider).ToList();

        Assert.Equal(refused, refusals.Count == 1);
    }

    // Review F1: Kestrel binds its default only when it is given no address, and a Host without a server binds none.
    [Fact]
    public async Task A_client_addressed_at_kestrels_default_url_starts_in_a_kestrel_host_listening_elsewhere()
    {
        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);
        await using var host = AppFixture.ComposeHost(
            "Development",
            Fixture.ConnectionString,
            new RecordedLogs(),
            builder =>
            {
                builder.WebHost.UseKestrel();
                AddClient(builder.Services, new Uri("http://localhost:5000"));
            },
            "--urls=http://127.0.0.1:0"
        );

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_client_addressed_at_kestrels_default_url_starts_in_a_host_without_a_server()
    {
        var builder = new HostApplicationBuilder(["--environment", "Development"]);
        builder.AddPouryaKitCompositionChecks();
        AddClient(builder.Services, new Uri("http://localhost:5000"));
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    // Review F1: a peer on the port this Host also listens on every address for, as two containers on 8080, is another Host.
    [Fact]
    public async Task A_client_addressed_at_another_host_on_a_port_its_own_host_listens_on_starts()
    {
        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);
        await using var host = AppFixture.ComposeHost(
            "Development",
            Fixture.ConnectionString,
            new RecordedLogs(),
            builder => AddClient(builder.Services, new Uri("http://inventory.local:8080")),
            "--http_ports=8080"
        );

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    // Review F5: ASP.NET Core listens on the urls instead of the ports, and on the Kestrel endpoints instead of the urls,
    // so a client at the overridden one, as a sidecar on the pod's localhost, calls another process.
    [Theory]
    [InlineData("--http_ports=8080 --urls=http://localhost:9000", "http://localhost:8080")]
    [InlineData("--urls=http://localhost:9000 --Kestrel:Endpoints:Grpc:Url=http://localhost:9100", "http://localhost:9000")]
    public async Task A_client_addressed_at_a_listening_url_its_host_overrides_starts(string listening, string address)
    {
        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);
        await using var host = AppFixture.ComposeHost(
            "Development",
            Fixture.ConnectionString,
            new RecordedLogs(),
            builder => AddClient(builder.Services, new Uri(address)),
            listening.Split(' ')
        );

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    // Review F4: a declared service no MapPouryaKitGrpcServices mapped would answer every call Unimplemented.
    [Fact]
    public async Task A_declared_service_that_is_never_mapped_fails_the_start()
    {
        var builder = WebApplication.CreateBuilder(["--environment", "Development"]);
        builder.WebHost.UseTestServer();
        builder.Services.AddPouryaKitCqrs();
        builder.Services.AddPouryaKitWeb();
        builder.Services.AddPouryaKitGrpcService<ProductGrpcService>();
        builder.AddPouryaKitCompositionChecks();
        await using var host = builder.Build();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            $"The gRPC services {typeof(ProductGrpcService).FullName} are declared but never mapped, so every call to them answers Unimplemented. Call app.MapPouryaKitGrpcServices() once the Host is built.",
            refused.Message
        );
    }

    // Review F2: a Kit header the call already carries never survives beside the context's own; here a tenant beside a
    // context without one, which the peer would otherwise restore.
    [Fact]
    public async Task A_kit_header_the_call_already_carries_is_replaced_by_the_contexts_own()
    {
        var product = await CreateProductAsync();
        var services = new ServiceCollection();
        AddClient(services, new Uri("http://peer.local"))
            .ConfigurePrimaryHttpMessageHandler(() => new Http2(Fixture.Host.GetTestServer().CreateHandler()))
            .AddHttpMessageHandler(() => new AsTrustedPeer([]));
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<ProductService.ProductServiceClient>();
        var options = new CallOptions(
            headers: new Metadata { { OperationContextHeaders.Tenant, "acme" } },
            cancellationToken: TestContext.Current.CancellationToken
        ).WithOperationContext(new OperationContext(Actor.Person("ada")));

        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.GetProductAsync(new GetProductRequest { ProductId = product.ToString() }, options)
        );

        Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
        Assert.Equal([TenantErrors.Required.Code], Errors(refused).Select(error => error.GetProperty("code").GetString()));
    }

    // PR #143: a second interceptor would look for the context on the first one's copy of the metadata and find none.
    [Fact]
    public async Task A_client_registered_twice_adds_nothing_the_second_time_and_its_call_carries_its_context()
    {
        var product = await CreateProductAsync();
        var services = new ServiceCollection();
        List<HttpRequestMessage> sent = [];
        AddClient(services, new Uri("http://peer.local"))
            .ConfigurePrimaryHttpMessageHandler(() => new Http2(Fixture.Host.GetTestServer().CreateHandler()))
            .AddHttpMessageHandler(() => new AsTrustedPeer(sent));
        var registrations = services.Count;
        services.AddPouryaKitGrpcClient<ProductService.ProductServiceClient>(Client);
        var registrationsAfter = services.Count;
        await using var provider = services.BuildServiceProvider();
        var options = new CallOptions(cancellationToken: TestContext.Current.CancellationToken).WithOperationContext(
            new OperationContext(Actor.Person("ada"), "acme")
        );

        var reply = await provider
            .GetRequiredService<ProductService.ProductServiceClient>()
            .GetProductAsync(new GetProductRequest { ProductId = product.ToString() }, options);

        Assert.Equal(Name(product), reply.Name);
        Assert.Equal(["acme"], Assert.Single(sent).Headers.GetValues(OperationContextHeaders.Tenant));
        Assert.Equal(registrations, registrationsAfter);
    }

    // PR #143: a service mapped twice would have two identical endpoints, and every call to it would be ambiguous.
    [Fact]
    public async Task A_second_mapping_of_the_services_maps_none_again_and_a_call_reaches_the_service()
    {
        var product = await CreateProductAsync();
        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);
        await using var host = AppFixture.ComposeHost("Development", Fixture.ConnectionString, Fixture.Logs);
        host.MapPouryaKitGrpcServices();
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var channel = Channel(host);

        var reply = await new ProductService.ProductServiceClient(channel).GetProductAsync(
            new GetProductRequest { ProductId = product.ToString() },
            Caller("grace", new Metadata { { TestAuthentication.TenantHeader, "acme" } }),
            cancellationToken: TestContext.Current.CancellationToken
        );
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Name(product), reply.Name);
    }

    [Fact]
    public async Task A_client_without_a_positive_deadline_fails_the_start()
    {
        await using var host = AppFixture.ComposeHost(
            "Development",
            Fixture.ConnectionString,
            new RecordedLogs(),
            builder =>
            {
                AddClient(builder.Services, new Uri("http://catalog.local"));
                builder.Services.Configure<PouryaKitGrpcClientOptions>(Client, options => options.Deadline = TimeSpan.Zero);
            }
        );

        var refused = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal([$"PouryaKitGrpcClientOptions.Deadline of the gRPC client {Client} is not positive."], refused.Failures);
    }

    // Ticket #159: a deadline the clock cannot reach is none.
    [Fact]
    public async Task A_client_whose_deadline_ends_after_the_latest_time_sends_its_call_with_no_deadline()
    {
        var product = await CreateProductAsync();
        var services = new ServiceCollection();
        List<HttpRequestMessage> sent = [];
        AddClient(services, new Uri("http://peer.local"))
            .ConfigurePrimaryHttpMessageHandler(() => new Http2(Fixture.Host.GetTestServer().CreateHandler()))
            .AddHttpMessageHandler(() => new AsTrustedPeer(sent));
        services.Configure<PouryaKitGrpcClientOptions>(Client, options => options.Deadline = TimeSpan.MaxValue);
        await using var provider = services.BuildServiceProvider();
        var options = new CallOptions(cancellationToken: TestContext.Current.CancellationToken).WithOperationContext(
            new OperationContext(Actor.Person("ada"), "acme")
        );

        var reply = await provider
            .GetRequiredService<ProductService.ProductServiceClient>()
            .GetProductAsync(new GetProductRequest { ProductId = product.ToString() }, options);

        Assert.Equal(Name(product), reply.Name);
        Assert.False(Assert.Single(sent).Headers.Contains("grpc-timeout"));
    }

    // A Host of the application whose Catalog client calls the peer Host's in-memory server, authenticated as the peer the
    // Hosts trust; the requests it sends are recorded.
    private WebApplication ComposeHost(Func<IHost> peer, List<HttpRequestMessage> sent) =>
        AppFixture.ComposeHost(
            "Development",
            Fixture.ConnectionString,
            Fixture.Logs,
            builder =>
                AddClient(builder.Services, new Uri("http://peer.local"))
                    .ConfigurePrimaryHttpMessageHandler(() => new Http2(peer().GetTestServer().CreateHandler()))
                    .AddHttpMessageHandler(() => new AsTrustedPeer(sent))
        );

    private static IHttpClientBuilder AddClient(IServiceCollection services, Uri address)
    {
        services.Configure<PouryaKitGrpcClientOptions>(
            Client,
            options =>
            {
                options.Address = address;
                options.Deadline = Deadline;
            }
        );
        return services.AddPouryaKitGrpcClient<ProductService.ProductServiceClient>(Client);
    }

    private static async Task<ProductReply> GetProductAsync(IHost host, Guid product, OperationContext? context)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<ProductService.ProductServiceClient>();
        var options = new CallOptions(cancellationToken: TestContext.Current.CancellationToken);
        return await client.GetProductAsync(
            new GetProductRequest { ProductId = product.ToString() },
            context is null ? options : options.WithOperationContext(context)
        );
    }

    private static void AssertContinued(OperationContext caller, OperationContext continued)
    {
        Assert.Equal(
            (caller.Actor, caller.Tenant, caller.CorrelationId, (Guid?)caller.OperationId),
            (continued.Actor, continued.Tenant, continued.CorrelationId, continued.CausationId)
        );
        Assert.NotEqual(caller.OperationId, continued.OperationId);
    }

    // A gRPC client of the fixture's Host that sends only the metadata each call names.
    private GrpcChannel Channel(IHost? host = null) =>
        GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = new Http2((host ?? Fixture.Host).GetTestServer().CreateHandler()) }
        );

    private static async Task<RpcException> FailureAsync(GrpcChannel channel, string productId, Metadata headers) =>
        await Assert.ThrowsAsync<RpcException>(async () =>
            await new ProductService.ProductServiceClient(channel).GetProductAsync(
                new GetProductRequest { ProductId = productId },
                headers,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

    private static Metadata ContextHeaders() =>
        new()
        {
            { OperationContextHeaders.ActorId, "ada" },
            { OperationContextHeaders.ActorKind, nameof(ActorKind.Person) },
            { OperationContextHeaders.Tenant, "acme" },
            { OperationContextHeaders.CorrelationId, Guid.CreateVersion7().ToString() },
            { OperationContextHeaders.CausationId, Guid.CreateVersion7().ToString() },
        };

    private static Metadata Caller(string caller, Metadata headers)
    {
        headers.Add("authorization", $"{TestAuthentication.SchemeName} {caller}");
        return headers;
    }

    private static List<JsonElement> Errors(RpcException failure)
    {
        using var errors = JsonDocument.Parse(Assert.Single(failure.Trailers, entry => entry.Key == ResultGrpcExtensions.ErrorsTrailer).ValueBytes);
        return [.. errors.RootElement.EnumerateArray().Select(error => error.Clone())];
    }

    private static string? Target(JsonElement error) => error.TryGetProperty("target", out var target) ? target.GetString() : null;

    // What is left of the call's deadline when it is sent, from grpc-timeout, whose unit is its last character.
    private static TimeSpan Timeout(HttpRequestMessage sent)
    {
        var timeout = Assert.Single(sent.Headers.GetValues("grpc-timeout"));
        var value = long.Parse(timeout[..^1], CultureInfo.InvariantCulture);
        return timeout[^1] switch
        {
            'H' => TimeSpan.FromHours(value),
            'M' => TimeSpan.FromMinutes(value),
            'S' => TimeSpan.FromSeconds(value),
            'm' => TimeSpan.FromMilliseconds(value),
            'u' => TimeSpan.FromMicroseconds(value),
            _ => TimeSpan.FromTicks(value / 100),
        };
    }

    // A loopback port nothing listens on now, for a Kestrel listener set in code, which cannot ask for port 0 on localhost.
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string Name(Guid product) => $"Lamp {product:N}";

    // In the tenant the Kit headers of these tests name.
    private async Task<Guid> CreateProductAsync()
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var context = new OperationContext(GatewayTestExtensions.TestActor, "acme");
        var category = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        Assert.True((await catalog.SendAsync(new CreateCategoryCommand(category, "Lamps"), context, TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await catalog.SendAsync(new CreateProductCommand(product, category, Name(product)), context, TestContext.Current.CancellationToken)).IsSuccess);
        return product;
    }

    // An in-memory server answers HTTP/1.1, which a gRPC client refuses; the response keeps the request's version.
    private sealed class Http2(HttpMessageHandler server) : DelegatingHandler(server)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            response.Version = request.Version;
            return response;
        }
    }

    private sealed class AsTrustedPeer(List<HttpRequestMessage> sent) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new(TestAuthentication.SchemeName, AppFixture.TrustedPeer);
            sent.Add(request);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
