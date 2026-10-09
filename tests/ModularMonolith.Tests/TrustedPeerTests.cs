using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Features.CreateProduct;
using PouryaKit.Cqrs;
using PouryaKit.Messaging;
using PouryaKit.Security;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

// The receiving side of a call between Kit Hosts, through the Host's real HTTP pipeline: the Operation Context headers,
// named by PouryaKit.Messaging's vocabulary, continue an Operation only for a caller authenticated as a configured peer.
public sealed class TrustedPeerTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private static readonly Guid Correlation = Guid.CreateVersion7();

    private static readonly Guid Causation = Guid.CreateVersion7();

    private const string Rule = "1 to 256 visible ASCII characters, with spaces only between them";

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

    // Final structural review R1 (ADR 0007's #57 amendment): the peer's request is a trusted continuation, so the
    // permission the command declares is not checked again for the continued Actor. Neither Ada nor the peer holds it
    // here. The same headers from a caller the Host does not trust are that caller's own request, and it is refused.
    [Fact]
    public async Task A_trusted_peer_continues_into_a_permission_protected_command_its_actor_holds_no_permission_for_and_an_untrusted_caller_is_refused()
    {
        var name = $"Lamp {Guid.CreateVersion7():N}";
        var lamp = await CreateProductAsync(name);
        using var peer = WithContextHeaders(Fixture.Client(AppFixture.TrustedPeer));
        using var person = WithContextHeaders(Fixture.Client("grace"));

        var refused = await RemoveAsync(person, name);
        var refusedRows = await Fixture.RowsAsync($"select is_deleted from catalog.products where id = '{lamp}'");
        var continued = await RemoveAsync(peer, name);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using (var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)))
        {
            Assert.Equal(
                [PermissionErrors.Denied.Code],
                problem.RootElement.GetProperty("errors").EnumerateArray().Select(error => error.GetProperty("code").GetString())
            );
        }
        Assert.Equal([[false]], refusedRows);
        Assert.Equal(HttpStatusCode.OK, continued.StatusCode);
        Assert.Equal("1", await continued.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        // Removed as the continued Actor, not as the peer.
        Assert.Equal(
            [[true, "ada"]],
            await Fixture.RowsAsync($"select is_deleted, deleted_by from catalog.products where id = '{lamp}'")
        );
    }

    // The Kit's own reason is the problem's detail: it names the header and the rule it broke.
    [Theory]
    [InlineData(OperationContextHeaders.CausationId, null, $"The request continues an Operation but has no single {OperationContextHeaders.CausationId} header that is {Rule}.")]
    [InlineData(OperationContextHeaders.CorrelationId, "not-a-guid", $"The request's {OperationContextHeaders.CorrelationId} header is not a Guid.")]
    [InlineData(OperationContextHeaders.ActorKind, "Robot", $"The request's {OperationContextHeaders.ActorKind} header Robot is not an ActorKind.")]
    [InlineData(OperationContextHeaders.ActorId, "ada\u0007", $"The request continues an Operation but has no single {OperationContextHeaders.ActorId} header that is {Rule}.")]
    [InlineData(OperationContextHeaders.Tenant, "acmé", $"The request continues an Operation but has no single {OperationContextHeaders.Tenant} header that is {Rule}.")]
    public async Task A_trusted_peer_whose_context_headers_are_incomplete_or_unreadable_is_refused_with_400(
        string header,
        string? value,
        string detail
    )
    {
        using var peer = WithContextHeaders(Fixture.Client(AppFixture.TrustedPeer));
        peer.DefaultRequestHeaders.Remove(header);
        if (value is not null)
        {
            // Unvalidated, as a raw peer could send it; the in-memory server passes it on as it is.
            peer.DefaultRequestHeaders.TryAddWithoutValidation(header, value);
        }

        var response = await peer.GetAsync("/stub/context", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(detail, problem.GetProperty("detail").GetString());
    }

    private async Task<Guid> CreateProductAsync(string name)
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var category = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        await catalog.SendAsync(new CreateCategoryCommand(category, "Lamps"), TestContext.Current.CancellationToken);
        await catalog.SendAsync(new CreateProductCommand(product, category, name), TestContext.Current.CancellationToken);
        return product;
    }

    private static Task<HttpResponseMessage> RemoveAsync(HttpClient client, string name) =>
        client.PostAsJsonAsync("/stub/catalog-products/removal", new { names = new[] { name } }, TestContext.Current.CancellationToken);

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
