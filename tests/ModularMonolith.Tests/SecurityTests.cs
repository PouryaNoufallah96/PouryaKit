using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Features.CreateProduct;
using Catalog.Products.Features.RemoveProducts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Modularity;
using PouryaKit.Security;
using PouryaKit.Testing;
using PouryaKit.Web;

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
    public void A_fallback_policy_the_application_set_before_the_kits_registration_is_kept()
    {
        var applications = new AuthorizationPolicyBuilder().RequireClaim("application").Build();
        var services = new ServiceCollection();
        services.AddAuthorization(authorization => authorization.FallbackPolicy = applications);

        services.AddPouryaKitWeb();

        using var provider = services.BuildServiceProvider();
        Assert.Same(applications, provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value.FallbackPolicy);
    }

    [Fact]
    public void A_host_whose_modules_declare_permissions_but_that_registers_no_permission_source_fails_when_built()
    {
        var thrown = Assert.Throws<AggregateException>(() =>
            AppFixture.ComposeHost(
                "Production",
                Fixture.ConnectionString,
                new RecordedLogs(),
                builder => builder.Services.RemoveAll<IActorPermissions>()
            )
        );

        Assert.Contains(nameof(IActorPermissions), thrown.Message);
    }

    [Fact]
    public void A_command_or_query_that_declares_a_permission_whose_check_no_composition_root_registered_fails_the_host_when_built()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            AppFixture.ComposeHost(
                "Production",
                Fixture.ConnectionString,
                new RecordedLogs(),
                // The Stub Module's application authorizer of StampNoteCommand stays: it is not the permission check.
                builder => RemovePermissionChecks(builder.Services, typeof(StampNoteCommand), typeof(ReadStampLedgerQuery))
            )
        );

        Assert.Equal(
            [UncheckedPermission(typeof(ReadStampLedgerQuery)), UncheckedPermission(typeof(StampNoteCommand))],
            thrown.Message.Split(Environment.NewLine).Order(StringComparer.Ordinal)
        );
    }

    [Fact]
    public async Task A_permission_declared_in_one_module_and_registered_by_another_modules_composition_root_builds()
    {
        await using var host = AppFixture.ComposeHost(
            "Production",
            Fixture.ConnectionString,
            new RecordedLogs(),
            builder =>
            {
                RemovePermissionChecks(builder.Services, typeof(StampNoteCommand), typeof(ReadStampLedgerQuery));
                builder.AddPouryaKitModules(modules => modules.Add<StubPermissionsModule>());
            }
        );

        // The check registered elsewhere still runs in the Stub Module's Operation.
        var refused = await host
            .Services.GetRequiredService<IGatewayProcessor<StubModule>>()
            .QueryAsync(new ReadStampLedgerQuery(), new OperationContext(Actor.Person("ada")), TestContext.Current.CancellationToken);

        Assert.Equal(PermissionErrors.Denied, Assert.Single(refused.Errors));
    }

    [Fact]
    public async Task A_host_whose_modules_declare_no_permission_builds_and_serves_without_any_permission_registration()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.AddPouryaKitModules(modules => modules.Add<QueryOnlyModule>());

        using var host = builder.Build();
        var served = await host
            .Services.GetRequiredService<IGatewayProcessor<QueryOnlyModule>>()
            .QueryAsync(new ReadOperationContextQuery(), new OperationContext(Actor.Person("ada")), TestContext.Current.CancellationToken);

        Assert.Equal(Actor.Person("ada"), served.Value.Actor);
        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(IActorPermissions));
    }

    [Fact]
    public async Task A_query_that_declares_a_permission_is_refused_to_an_actor_without_it_and_served_to_one_with_it()
    {
        var stub = Fixture.Gateway<StubModule>();

        var refused = await stub.QueryAsync(
            new ReadStampLedgerQuery(),
            new OperationContext(Actor.Person("ada")),
            TestContext.Current.CancellationToken
        );
        // The tests' own Actor is granted it (TestActorPermissions).
        var served = await stub.QueryAsync(new ReadStampLedgerQuery(), TestContext.Current.CancellationToken);

        Assert.Equal(PermissionErrors.Denied, Assert.Single(refused.Errors));
        Assert.Equal(ReadStampLedgerQuery.Ledger, served.Value);
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
                    (context.Actor, context.Tenant, context.CorrelationId, context.IsTrustedContinuation)
                )
        );
    }

    [Fact]
    public async Task A_context_application_code_builds_with_a_causation_is_not_trusted_and_its_actor_is_checked()
    {
        var text = Unique();
        var ada = Actor.Person("ada");
        var stub = Fixture.Gateway<StubModule>();

        var command = await stub.SendAsync(
            new StampNoteCommand(text),
            new OperationContext(ada, causationId: Guid.CreateVersion7()),
            TestContext.Current.CancellationToken
        );
        var query = await stub.QueryAsync(
            new ReadStampLedgerQuery(),
            new OperationContext(ada, correlationId: Guid.CreateVersion7(), causationId: Guid.CreateVersion7()),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(PermissionErrors.Denied, Assert.Single(command.Errors));
        Assert.Equal(PermissionErrors.Denied, Assert.Single(query.Errors));
        Assert.Equal(0L, await StampsOfAsync(text));
    }

    [Fact]
    public async Task A_running_authorized_operation_continues_into_a_permission_protected_query_without_its_actor_being_checked_again()
    {
        var text = Unique();

        // Ada holds no permission to read the ledger: sent by her directly, the query is refused.
        var answered = await Fixture
            .Gateway<StubModule>()
            .QueryAsync(new AskStampLedgerQuery(text), new OperationContext(Actor.Person("ada")), TestContext.Current.CancellationToken);
        var probe = Fixture.Host.Services.GetRequiredService<ContinuationProbes>().For(text);
        probe.Ended.SetResult();

        Assert.Empty(answered.Errors);
        Assert.Equal(ReadStampLedgerQuery.Ledger, answered.Value);
        // Not while the checks of a query nested in it ran, before that one was authorized, nor once it had ended.
        Assert.False(probe.WhileChecking);
        Assert.False(await probe.AfterItEnded!);
    }

    [Fact]
    public async Task A_context_continued_outside_the_operation_that_ran_it_is_not_trusted()
    {
        var text = Unique();
        var stub = Fixture.Gateway<StubModule>();
        var neverRun = new OperationContext(Actor.Person("ada"));
        var ran = new OperationContext(Actor.Person("ada"));
        Assert.True((await stub.QueryAsync(new ReadOperationContextQuery(), ran, TestContext.Current.CancellationToken)).IsSuccess);

        Result[] refused =
        [
            await stub.SendAsync(new StampNoteCommand(text), neverRun.Continue(), TestContext.Current.CancellationToken),
            await stub.QueryAsync(new ReadStampLedgerQuery(), neverRun.Continue(), TestContext.Current.CancellationToken),
            await stub.SendAsync(new StampNoteCommand(text), ran.Continue(), TestContext.Current.CancellationToken),
            await stub.QueryAsync(new ReadStampLedgerQuery(), ran.Continue(), TestContext.Current.CancellationToken),
        ];

        Assert.All(refused, result => Assert.Equal(PermissionErrors.Denied, Assert.Single(result.Errors)));
        Assert.Equal(0L, await StampsOfAsync(text));
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

    // Leaves every other authorizer of the operations registered.
    private static void RemovePermissionChecks(IServiceCollection services, params Type[] operations)
    {
        var checks = operations.Select(operation => typeof(IAuthorizer<>).MakeGenericType(operation)).ToHashSet();
        foreach (
            var check in services
                .Where(descriptor =>
                    !descriptor.IsKeyedService
                    && checks.Contains(descriptor.ServiceType)
                    && descriptor.ImplementationType?.Assembly == typeof(RequiresPermissionAttribute).Assembly
                )
                .ToList()
        )
        {
            services.Remove(check);
        }
    }

    private static string UncheckedPermission(Type operation) =>
        $"{operation.FullName} declares RequiresPermissionAttribute, but its check is not registered, so it would run unchecked. Call AddPouryaKitPermissions with the assembly ModularMonolith.Tests in the Composition Root of StubModule.";

    private static string Unique() => Guid.CreateVersion7().ToString("N");

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return problem.RootElement.Clone();
    }
}

// Registers the permission checks of the Stub Module's commands and queries from another Module's Composition Root.
public sealed class StubPermissionsModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module) => module.AddPouryaKitPermissions(typeof(StubModule).Assembly);
}
