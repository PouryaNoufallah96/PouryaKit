using System.Net;
using System.Net.Http.Json;
using PouryaKit.Cqrs;
using PouryaKit.Messaging;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

// The receiving side of a call between Kit Hosts, through the Host's real HTTP pipeline: the Operation Context headers,
// named by PouryaKit.Messaging's vocabulary, continue an Operation only for a caller authenticated as a configured peer.
public sealed class TrustedPeerTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private static readonly Guid Correlation = Guid.CreateVersion7();

    private static readonly Guid Causation = Guid.CreateVersion7();

    [Fact]
    public async Task A_trusted_authenticated_peer_continues_the_operation_its_headers_name()
    {
        using var peer = WithContextHeaders(Fixture.Client(AppFixture.TrustedPeer));
        using var peerWithoutHeaders = Fixture.Client(AppFixture.TrustedPeer);

        var continued = await ContextAsync(peer, "/stub/context");
        var own = await ContextAsync(peerWithoutHeaders, "/stub/context");

        Assert.Equal(new SeenActor("ada", ActorKind.Person), continued.Actor);
        Assert.Equal("acme", continued.Tenant);
        Assert.Equal(Correlation, continued.CorrelationId);
        Assert.Equal(Causation, continued.CausationId);
        Assert.NotEqual(Causation, continued.OperationId);
        // Without the headers the peer runs as itself, in a chain of its own.
        Assert.Equal(new SeenActor(AppFixture.TrustedPeer, ActorKind.Person), own.Actor);
        Assert.Equal(own.OperationId, own.CorrelationId);
        Assert.Null(own.CausationId);
    }

    [Fact]
    public async Task An_untrusted_or_anonymous_caller_with_the_same_headers_keeps_its_own_identity_and_a_new_chain()
    {
        using var person = WithContextHeaders(Fixture.Client("grace"));
        using var otherHost = WithContextHeaders(Fixture.Client("billing-host"));
        using var anonymous = WithContextHeaders(Fixture.Client());

        SeenContext[] seen =
        [
            await ContextAsync(person, "/stub/context"),
            await ContextAsync(otherHost, "/stub/context"),
            await ContextAsync(anonymous, "/stub/public/context"),
        ];

        Assert.Equal(
            [
                new SeenActor("grace", ActorKind.Person),
                new SeenActor("billing-host", ActorKind.Person),
                new SeenActor(Stub.ReadOperationContextEndpoint.Visitor.Id, ActorKind.System),
            ],
            seen.Select(context => context.Actor)
        );
        Assert.All(
            seen,
            context =>
            {
                Assert.Null(context.Tenant);
                Assert.Equal(context.OperationId, context.CorrelationId);
                Assert.Null(context.CausationId);
            }
        );
    }

    [Theory]
    [InlineData(OperationContextHeaders.CausationId, null)]
    [InlineData(OperationContextHeaders.CorrelationId, "not-a-guid")]
    [InlineData(OperationContextHeaders.ActorKind, "Robot")]
    public async Task A_trusted_peer_whose_context_headers_are_incomplete_or_unreadable_is_refused_with_400(
        string header,
        string? value
    )
    {
        using var peer = WithContextHeaders(Fixture.Client(AppFixture.TrustedPeer));
        peer.DefaultRequestHeaders.Remove(header);
        if (value is not null)
        {
            peer.DefaultRequestHeaders.Add(header, value);
        }

        var response = await peer.GetAsync("/stub/context", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    private static HttpClient WithContextHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.Add(OperationContextHeaders.ActorId, "ada");
        client.DefaultRequestHeaders.Add(OperationContextHeaders.ActorKind, nameof(ActorKind.Person));
        client.DefaultRequestHeaders.Add(OperationContextHeaders.Tenant, "acme");
        client.DefaultRequestHeaders.Add(OperationContextHeaders.CorrelationId, Correlation.ToString());
        client.DefaultRequestHeaders.Add(OperationContextHeaders.CausationId, Causation.ToString());
        return client;
    }

    private static async Task<SeenContext> ContextAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SeenContext>(TestContext.Current.CancellationToken))!;
    }
}
