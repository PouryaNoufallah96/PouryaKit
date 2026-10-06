using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Features.CreateProduct;
using Catalog.Products.Features.RemoveProducts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Security;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

// Authenticated HTTP by default, permissions a command declares, and continuations that run on the authority of the
// Operation that caused them (G.6).
public sealed class SecurityTests(AppFixture app) : HostTest<AppFixture>(app)
{
    [Fact]
    public async Task An_endpoint_outside_every_module_requires_an_authenticated_caller_unless_it_allows_anonymous_ones()
    {
        await using var host = AppFixture.ComposeHost("Production", Fixture.ConnectionString, new RecordedLogs());
        host.MapGet("/outside", () => "served");
        host.MapGet("/outside/public", () => "served").AllowAnonymous();
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var anonymous = AppFixture.ClientOf(host);
        using var ada = AppFixture.ClientOf(host, "ada");

        var refused = await anonymous.GetAsync("/outside", TestContext.Current.CancellationToken);

        Assert.Equal(401, (await ProblemAsync(refused, HttpStatusCode.Unauthorized)).GetProperty("status").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/outside/public", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ada.GetAsync("/outside", TestContext.Current.CancellationToken)).StatusCode);
        // A path no route matches has no endpoint either: the fallback policy answers before routing's 404.
        await ProblemAsync(await anonymous.GetAsync("/nowhere", TestContext.Current.CancellationToken), HttpStatusCode.Unauthorized);
        await ProblemAsync(await ada.GetAsync("/nowhere", TestContext.Current.CancellationToken), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_caller_holding_the_declared_permission_commits_the_command()
    {
        var lamp = await CreateLampAsync();
        using var client = Fixture.Client("ada", permissions: RemoveProductsCommand.Permission);

        var response = await client.PostAsJsonAsync(
            "/stub/catalog-products/removal",
            new { names = new[] { "Lamp" } },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("1", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal([[true, "ada"]], await Fixture.RowsAsync($"select is_deleted, deleted_by from catalog.products where id = '{lamp}'"));
    }

    [Fact]
    public async Task A_caller_without_the_declared_permission_is_forbidden_with_a_403_problem_response_and_nothing_changes()
    {
        var lamp = await CreateLampAsync();
        using var client = Fixture.Client("ada", permissions: "catalog.products.other");

        var response = await client.PostAsJsonAsync(
            "/stub/catalog-products/removal",
            new { names = new[] { "Lamp" } },
            TestContext.Current.CancellationToken
        );

        var problem = await ProblemAsync(response, HttpStatusCode.Forbidden);
        Assert.Equal(
            [PermissionErrors.Denied.Code],
            problem.GetProperty("errors").EnumerateArray().Select(error => error.GetProperty("code").GetString())
        );
        Assert.Equal([[false, 1L]], await Fixture.RowsAsync($"select is_deleted, version from catalog.products where id = '{lamp}'"));
    }

    [Fact]
    public async Task A_continuation_runs_a_permission_protected_command_on_its_recorded_authority_without_authorizing_the_actor_again()
    {
        var text = Unique();
        var ada = Actor.Person("ada");

        // Ada holds no permission to stamp: sent by her, the command is refused.
        var direct = await Fixture
            .Gateway<StubModule>()
            .SendAsync(new StampNoteCommand(text), new OperationContext(ada), TestContext.Current.CancellationToken);
        var (_, deliveries) = await Fixture.WaitForDeliveriesAsync(
            () =>
                Fixture
                    .Gateway<StubModule>()
                    .SendAsync(
                        new WriteNoteRaisingCommand(text, [new NoteToStampDomainEvent(text)]),
                        new OperationContext(ada),
                        TestContext.Current.CancellationToken
                    ),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(PermissionErrors.Denied, Assert.Single(direct.Errors));
        Assert.False(Assert.Single(deliveries).DeadLettered);
        Assert.Equal(1L, await StampsOfAsync(text));
    }

    [Fact]
    public async Task An_application_authorizer_still_runs_for_a_continued_command_with_the_original_actor_and_can_refuse_it()
    {
        var text = StampNoteCommand.RefusedPrefix + Unique();
        var raising = new OperationContext(Actor.Person("ada"), tenant: "acme");

        var (_, deliveries) = await Fixture.WaitForDeliveriesAsync(
            () =>
                Fixture
                    .Gateway<StubModule>()
                    .SendAsync(
                        new WriteNoteRaisingCommand(text, [new NoteToStampDomainEvent(text)]),
                        raising,
                        TestContext.Current.CancellationToken
                    ),
            TestContext.Current.CancellationToken
        );

        Assert.True(Assert.Single(deliveries).DeadLettered);
        Assert.Equal(0L, await StampsOfAsync(text));
        var seen = Fixture.Host.Services.GetRequiredService<StampAuthorizations>().For(text);
        Assert.NotEmpty(seen);
        Assert.All(
            seen,
            context =>
                Assert.Equal(
                    (raising.Actor, raising.Tenant, raising.CorrelationId, true),
                    (context.Actor, context.Tenant, context.CorrelationId, context.CausationId is not null)
                )
        );
    }

    private async Task<Guid> CreateLampAsync()
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var lamps = Guid.CreateVersion7();
        var lamp = Guid.CreateVersion7();
        await catalog.SendAsync(new CreateCategoryCommand(lamps, "Lamps"), TestContext.Current.CancellationToken);
        await catalog.SendAsync(new CreateProductCommand(lamp, lamps, "Lamp"), TestContext.Current.CancellationToken);
        return lamp;
    }

    private Task<long> StampsOfAsync(string text) =>
        Fixture.ScalarAsync<long>($"select count(*) from stub.notes where text = '{StampNoteCommand.Stamped(text)}'");

    private static string Unique() => Guid.CreateVersion7().ToString("N");

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return problem.RootElement.Clone();
    }
}
