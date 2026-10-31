using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using ModularMonolith.Tests.Unversioned;
using PouryaKit.Cqrs;
using PouryaKit.Messaging;
using PouryaKit.Testing;
using PouryaKit.Web;

namespace ModularMonolith.Tests;

// Web's two members another transport builds on: the begin-the-Operation convention on a route group outside every
// Module's, and the header pairs of its one Operation Context codec; and the Module endpoints' mapping, called twice.
public sealed class WebConventionTests(AppFixture app) : HostTest<AppFixture>(app)
{
    [Fact]
    public async Task A_root_group_that_begins_the_operation_gives_its_endpoints_the_context_a_module_endpoint_gets()
    {
        var correlation = Guid.CreateVersion7();
        var causation = Guid.CreateVersion7();
        await using var host = AppFixture.ComposeHost(
            "Development",
            Fixture.ConnectionString,
            Fixture.Logs
        );
        new ReadOperationContextEndpoint().Map(
            host.MapGroup("/outside").RequireAuthorization().BeginOperationPerRequest()
        );
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var peer = AppFixture.ClientOf(host, AppFixture.TrustedPeer);
        peer.DefaultRequestHeaders.Add(OperationContextHeaders.ActorId, "ada");
        peer.DefaultRequestHeaders.Add(OperationContextHeaders.ActorKind, nameof(ActorKind.Person));
        peer.DefaultRequestHeaders.Add(OperationContextHeaders.Tenant, "acme");
        peer.DefaultRequestHeaders.Add(
            OperationContextHeaders.CorrelationId,
            correlation.ToString()
        );
        peer.DefaultRequestHeaders.Add(OperationContextHeaders.CausationId, causation.ToString());
        using var person = AppFixture.ClientOf(host, "grace", "acme");
        using var anonymous = AppFixture.ClientOf(host);

        var continued = await ContextAsync(peer, "/outside/context");
        var own = await ContextAsync(person, "/outside/context");
        var visitor = await ContextAsync(anonymous, "/outside/public/context");
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new SeenActor("ada", ActorKind.Person), continued.Actor);
        Assert.Equal("acme", continued.Tenant);
        Assert.Equal(correlation, continued.CorrelationId);
        Assert.Equal(causation, continued.CausationId);
        Assert.NotEqual(causation, continued.OperationId);
        Assert.Equal(new SeenActor("grace", ActorKind.Person), own.Actor);
        Assert.Equal("acme", own.Tenant);
        Assert.Equal(own.OperationId, own.CorrelationId);
        Assert.Null(own.CausationId);
        Assert.Equal(
            new SeenActor(ReadOperationContextEndpoint.Visitor.Id, ActorKind.System),
            visitor.Actor
        );
    }

    [Fact]
    public async Task A_nested_group_that_also_begins_the_operation_serves_its_endpoints_with_one_operation()
    {
        await using var host = AppFixture.ComposeHost(
            "Development",
            Fixture.ConnectionString,
            Fixture.Logs
        );
        var root = host.MapGroup("/root").RequireAuthorization().BeginOperationPerRequest();
        new ReadOperationContextEndpoint().Map(root.MapGroup("/nested").BeginOperationPerRequest());
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var person = AppFixture.ClientOf(host, "grace", "acme");

        var nested = await ContextAsync(person, "/root/nested/context");
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new SeenActor("grace", ActorKind.Person), nested.Actor);
        Assert.Equal("acme", nested.Tenant);
        Assert.Equal(nested.OperationId, nested.CorrelationId);
    }

    [Fact]
    public async Task Module_endpoints_mapped_under_a_root_group_that_begins_the_operation_begin_it_once()
    {
        await using var host = PlainEndpointsHost();
        host.MapGroup("/root").BeginOperationPerRequest().MapPouryaKitModuleEndpoints();
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var person = AppFixture.ClientOf(host, "grace");

        var response = await person.GetAsync(
            "/root/plain/actor",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "grace",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
    }

    // Two identical endpoints would make every request to their route ambiguous, as MapPouryaKitGrpcServices avoids too.
    [Fact]
    public async Task A_second_mapping_of_the_module_endpoints_maps_none_again_and_a_request_reaches_the_endpoint()
    {
        await using var host = PlainEndpointsHost();
        host.MapPouryaKitModuleEndpoints();
        host.MapPouryaKitModuleEndpoints();
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var person = AppFixture.ClientOf(host, "grace");

        var response = await person.GetAsync("/plain/actor", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "grace",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        Assert.Single(
            host.Services.GetRequiredService<EndpointDataSource>().Endpoints,
            endpoint => endpoint is RouteEndpoint { RoutePattern.RawText: "/plain/actor" }
        );
    }

    [Fact]
    public async Task The_public_header_pairs_are_the_ones_the_outgoing_handler_writes_in_messagings_names()
    {
        var context = new OperationContext(Actor.Person("ada"), "acme");
        var sent = new SentHeaders();
        var services = new ServiceCollection();
        services
            .AddHttpClient("peer")
            .AddPouryaKitOperationContext()
            .ConfigurePrimaryHttpMessageHandler(() => sent);
        await using var provider = services.BuildServiceProvider();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "http://peer.test/"
        ).WithOperationContext(context);

        using var response = await provider
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("peer")
            .SendAsync(request, TestContext.Current.CancellationToken);

        var pairs = OperationContextHttpHeaders.Pairs(context);
        Assert.Equal(
            [
                new(OperationContextHeaders.ActorId, "ada"),
                new(OperationContextHeaders.ActorKind, nameof(ActorKind.Person)),
                new(OperationContextHeaders.Tenant, "acme"),
                new(OperationContextHeaders.CorrelationId, context.CorrelationId.ToString()),
                new(OperationContextHeaders.CausationId, context.OperationId.ToString()),
            ],
            pairs
        );
        Assert.Equal(pairs, sent.Headers);
        // A value the receiving Host would refuse is refused before any pair is returned.
        Assert.Throws<InvalidOperationException>(() =>
            OperationContextHttpHeaders.Pairs(new OperationContext(Actor.Person("ada"), "acmé"))
        );
    }

    // A Host of the endpoints of the Unversioned assembly under /plain, with the test scheme and the Kit's checks.
    private static WebApplication PlainEndpointsHost()
    {
        var builder = WebApplication.CreateBuilder(["--environment", "Development"]);
        builder.WebHost.UseTestServer();
        builder
            .Services.AddAuthentication(TestAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthentication>(
                TestAuthentication.SchemeName,
                null
            );
        builder.Services.AddAuthorization();
        builder.Services.AddPouryaKitCqrs();
        builder.Services.AddPouryaKitWeb();
        builder.Services.AddPouryaKitEndpoints("/plain", typeof(UnversionedEndpoints).Assembly);
        builder.AddPouryaKitCompositionChecks();
        var host = builder.Build();
        host.UseRouting();
        host.UseAuthentication();
        host.UseAuthorization();
        return host;
    }

    private static async Task<SeenContext> ContextAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (
            await response.Content.ReadFromJsonAsync<SeenContext>(
                TestContext.Current.CancellationToken
            )
        )!;
    }

    // The peer's end of the call: the Kit's headers as the request arrived, in the order they were written.
    private sealed class SentHeaders : HttpMessageHandler
    {
        public List<KeyValuePair<string, string>> Headers { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Headers.AddRange(
                request
                    .Headers.Where(header =>
                        header.Key.StartsWith("pouryakit-", StringComparison.Ordinal)
                    )
                    .Select(header => new KeyValuePair<string, string>(
                        header.Key,
                        Assert.Single(header.Value)
                    ))
            );
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
