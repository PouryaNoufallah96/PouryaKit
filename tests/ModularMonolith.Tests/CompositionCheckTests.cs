using Billing;
using Catalog;
using Catalog.Infrastructure.Data.Context;
using Catalog.Products.Features.GetProduct;
using Inventory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ModularMonolith.Tests.Misnamed;
using ModularMonolith.Tests.Stub;
using ModularMonolith.Tests.Unowned;
using ModularMonolith.Tests.Unversioned;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Modularity;
using PouryaKit.Persistence.Postgres;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

// The composition checks AddPouryaKitCompositionChecks runs when the Host's container is built (ticket #61): a Module's
// declaration made without its Module, a cacheable query without caching, a command without a Unit of Work; and, when
// the Host starts, a container a later ConfigureContainer replaced, which ran none of them.
public sealed class CompositionCheckTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private const string TheModule = "typeof(TheModule))";

    // One for each Module-aware declaration whose Module parameter #61 removed, made on the Host's services, where it
    // belongs to the application, in a Host of Modules.
    private static readonly Dictionary<string, (Action<IServiceCollection> Declare, string Problem)> Unowned = new()
    {
        ["Publish"] = (
            services => services.Publish<UnownedV1>("tests.unowned.v1"),
            $"The Integration Event {typeof(UnownedV1).FullName} is declared without a Module in a Host of Modules, so it runs in no Module and publishing it would fail once the Host serves. Declare it in its Module's Composition Root with Publish<UnownedV1>(wireName), or name the Module in the Host's composition with Publish<UnownedV1>(wireName, {TheModule}."
        ),
        ["PublishSnapshot"] = (
            services => services.PublishSnapshot<Stub.Shelf, MislaidShelfSnapshotV1, MislaidShelfSnapshotMapper>("tests.mislaid-shelf.v1"),
            $"The Snapshot {typeof(MislaidShelfSnapshotV1).FullName} of {typeof(Stub.Shelf).FullName} is declared without a Module in a Host of Modules, so it runs in no Module and a commit that changes its aggregate would fail once the Host serves. Declare it in its Module's Composition Root with PublishSnapshot<Shelf, MislaidShelfSnapshotV1, MislaidShelfSnapshotMapper>(wireName), or name the Module in the Host's composition with PublishSnapshot<Shelf, MislaidShelfSnapshotV1, MislaidShelfSnapshotMapper>(wireName, {TheModule}."
        ),
        ["Subscribe"] = (
            services => services.Subscribe<NoteSharedV1, RecordSharedNoteIntegrationEventHandler>("tests.unowned-shared"),
            $"The subscription tests.unowned-shared of {typeof(RecordSharedNoteIntegrationEventHandler).FullName} is declared without a Module in a Host of Modules, so it runs in no Module and its deliveries would fail once the Host serves. Declare it in its Module's Composition Root with Subscribe<NoteSharedV1, RecordSharedNoteIntegrationEventHandler>(subscriptionName), or name the Module in the Host's composition with Subscribe<NoteSharedV1, RecordSharedNoteIntegrationEventHandler>(subscriptionName, {TheModule}."
        ),
        ["AddPouryaKitNotificationHandlers"] = (
            services => services.AddPouryaKitNotificationHandlers(typeof(UnownedNotificationHandler).Assembly),
            $"The Notification Handler {typeof(UnownedNotificationHandler).FullName}, subscribed as {MessageNames.SubscriptionNameOf(typeof(UnownedNotificationHandler), typeof(UnownedDomainEvent))}, is declared without a Module in a Host of Modules, so it runs in no Module and its deliveries would fail once the Host serves. Declare it in its Module's Composition Root with AddPouryaKitNotificationHandlers(), or name the Module in the Host's composition with AddPouryaKitNotificationHandlers({TheModule}."
        ),
        ["AddPouryaKitDelayedCommand"] = (
            services => services.AddPouryaKitDelayedCommand<WriteNoteCommand>("tests.unowned-delayed"),
            $"The delayed command tests.unowned-delayed, {typeof(WriteNoteCommand).FullName}, is declared without a Module in a Host of Modules, so it runs in no Module and scheduling it would fail once the Host serves. Declare it in its Module's Composition Root with AddPouryaKitDelayedCommand<WriteNoteCommand>(name), or name the Module in the Host's composition with AddPouryaKitDelayedCommand<WriteNoteCommand>(name, {TheModule}."
        ),
        ["AddPouryaKitRecurringCommand"] = (
            services =>
                services
                    .AddPouryaKitRecurringCommand("tests.unowned-recurring", "tests.unowned", at => new WriteNoteCommand($"at {at:O}"))
                    .Configure(options => options.Every = TimeSpan.FromHours(1)),
            $"The recurring command tests.unowned-recurring, {typeof(WriteNoteCommand).FullName}, is declared without a Module in a Host of Modules, so it runs in no Module and its occurrences would fail once the Host serves. Declare it in its Module's Composition Root with AddPouryaKitRecurringCommand<WriteNoteCommand>(name, systemActor, occurrence), or name the Module in the Host's composition with AddPouryaKitRecurringCommand<WriteNoteCommand>(name, systemActor, occurrence, {TheModule}."
        ),
    };

    public static TheoryData<string> UnownedDeclarations => [.. Unowned.Keys];

    [Theory]
    [MemberData(nameof(UnownedDeclarations))]
    public void A_module_declaration_made_without_its_module_in_a_host_of_modules_fails_the_host_when_built(string declaration)
    {
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            AppFixture.ComposeHost("Development", Fixture.ConnectionString, new RecordedLogs(), builder => Unowned[declaration].Declare(builder.Services))
        );

        Assert.Equal(Unowned[declaration].Problem, thrown.Message);
    }

    [Fact]
    public void A_composition_root_that_names_another_module_is_refused_while_the_host_is_composed()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.AddPouryaKitModules(modules => modules.Add<OtherModuleNamingModule>()));

        Assert.Equal(
            "The Composition Root of OtherModuleNamingModule declares for StubModule. A Composition Root declares only for its own Module and names none; the Host's composition names the Module of a declaration it makes on a Module's behalf.",
            thrown.Message
        );
    }

    [Fact]
    public async Task A_container_that_a_later_configure_container_replaced_stops_the_host_at_start_naming_the_call()
    {
        await using var host = AppFixture.ComposeHost(
            "Development",
            Fixture.ConnectionString,
            new RecordedLogs(),
            builder => ((IHostApplicationBuilder)builder).ConfigureContainer(new DefaultServiceProviderFactory())
        );

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.StartsWith(
            "The Host's container was built by another service provider factory than the Kit's: a ConfigureContainer call after AddPouryaKitCompositionChecks, which AddPouryaKitModules and UsePouryaKitWolverine call, replaced it.",
            thrown.Message
        );
    }

    // A check may throw instead of answering, as the message store's does on a connection string it cannot read. The
    // container disposes what it created last first, so the service whose disposal fails is resolved first.
    [Fact]
    public void A_composition_check_that_throws_fails_the_build_with_its_own_exception_and_the_container_is_disposed()
    {
        var resolved = new DisposalProbe();
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.Services.AddSingleton(_ => new FailingDisposal());
        builder.Services.AddSingleton(_ => resolved);
        builder.Services.AddSingleton<ICompositionCheck, ThrowingCheck>();
        builder.AddPouryaKitCompositionChecks();

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(ThrowingCheck.Problem, thrown.Message);
        Assert.True(resolved.Disposed);
    }

    // The other way a check fails the build: it answers with its problems, which outlast a singleton whose disposal
    // throws too.
    [Fact]
    public void Composition_problems_fail_the_build_with_every_problem_though_a_singletons_disposal_throws()
    {
        var resolved = new DisposalProbe();
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.Services.AddSingleton(_ => new FailingDisposal());
        builder.Services.AddSingleton(_ => resolved);
        builder.Services.AddSingleton<ICompositionCheck>(new ReportingCheck(typeof(FailingDisposal)));
        builder.AddPouryaKitCompositionChecks();

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(string.Join(Environment.NewLine, ReportingCheck.Problems), thrown.Message);
        Assert.True(resolved.Disposed);
    }

    // The failed build's container is disposed as the Host disposes its own, asynchronously: the container's Dispose
    // throws for a singleton that is only IAsyncDisposable and would leave it, and whatever was created before it,
    // undisposed.
    [Fact]
    public void A_failed_build_disposes_a_singleton_that_can_only_be_disposed_asynchronously()
    {
        var first = new DisposalProbe();
        var asyncOnly = new AsyncOnlyDisposal();
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.Services.AddSingleton(_ => first);
        builder.Services.AddSingleton(_ => asyncOnly);
        builder.Services.AddSingleton<ICompositionCheck>(new ReportingCheck(typeof(DisposalProbe), typeof(AsyncOnlyDisposal)));
        builder.AddPouryaKitCompositionChecks();

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(string.Join(Environment.NewLine, ReportingCheck.Problems), thrown.Message);
        Assert.True(asyncOnly.Disposed);
        Assert.True(first.Disposed);
    }

    // A thread with a synchronization context, as a desktop application's has: the disposal's continuation is posted to
    // the context, whose thread is the one that waits for it. The context here runs nothing it is given, as a blocked
    // thread does.
    [Fact]
    public void A_failed_build_on_a_thread_with_a_synchronization_context_still_ends_and_disposes_what_awaits()
    {
        var awaiting = new AwaitingDisposal();
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.Services.AddSingleton(_ => awaiting);
        builder.Services.AddSingleton<DisposalProbe>();
        builder.Services.AddSingleton<ICompositionCheck>(new ReportingCheck(typeof(AwaitingDisposal)));
        builder.AddPouryaKitCompositionChecks();
        var blocked = new BlockedContext();
        Exception? thrown = null;
        SynchronizationContext? afterwards = null;
        var building = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(blocked);
            thrown = Record.Exception(() => builder.Build());
            afterwards = SynchronizationContext.Current;
        })
        {
            IsBackground = true,
        };

        building.Start();

        Assert.True(building.Join(TimeSpan.FromSeconds(30)), "The failed build never ended: its disposal waits for the thread it blocks.");
        Assert.Equal(string.Join(Environment.NewLine, ReportingCheck.Problems), Assert.IsType<InvalidOperationException>(thrown).Message);
        Assert.True(awaiting.Disposed);
        // The thread has its own context back once the wait is over.
        Assert.Same(blocked, afterwards);
    }

    [Fact]
    public void A_cacheable_query_in_a_host_without_caching_fails_the_host_when_built_naming_the_call()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            AppFixture.ComposeHost("Development", Fixture.ConnectionString, new RecordedLogs(), builder => builder.Services.RemoveAll<IQueryCache>())
        );

        Assert.StartsWith("The Host registers no query cache, so every cacheable query would run uncached: ", thrown.Message);
        Assert.Contains(typeof(GetProductQuery).FullName!, thrown.Message);
        Assert.Contains(typeof(CountNotesQuery).FullName!, thrown.Message);
        Assert.EndsWith(". Call AddPouryaKitCaching() on the Host's services.", thrown.Message);
    }

    [Fact]
    public void A_features_command_in_a_host_without_a_unit_of_work_fails_the_host_when_built()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.Services.AddPouryaKitCqrs(typeof(UnownedCommand).Assembly);
        builder.AddPouryaKitCompositionChecks();

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(
            $"The Host handles the command {typeof(UnownedCommand).FullName} but registers no Unit of Work, so the command would fail when it runs. Register the Unit of Work of a persistence package, for example with UsePouryaKitPostgres.",
            thrown.Message
        );
    }

    // A scanned handler of a command from an assembly nothing scans, as a Contracts assembly's, counts (#61 review
    // finding F7).
    [Fact]
    public void A_scanned_handler_of_a_command_from_another_assembly_in_a_host_without_a_unit_of_work_fails_the_host_when_built()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.Services.AddPouryaKitCqrs(typeof(SeparateCommandHandler).Assembly);
        builder.AddPouryaKitCompositionChecks();

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(
            $"The Host handles the command {typeof(UnownedCommand).FullName} but registers no Unit of Work, so the command would fail when it runs. Register the Unit of Work of a persistence package, for example with UsePouryaKitPostgres.",
            thrown.Message
        );
    }

    // The Kit's own delivery commands, which UsePouryaKitWolverine registers, run only for a declaration: a Host that
    // declares none needs no Unit of Work (#61 review finding F3).
    [Fact]
    public async Task A_host_with_wolverine_and_no_declaration_or_unit_of_work_builds_and_starts()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Development"]);
        builder.UsePouryaKitWolverine();
        builder.AddPouryaKitCompositionChecks();
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    // Each delivery runs in its Module's Unit of Work, where its Receipt is recorded (the final structural review of
    // Wave 2, finding 3). The Host's other Modules, each with its own, add no line.
    [Fact]
    public void A_module_that_subscribes_but_registers_no_unit_of_work_fails_the_host_when_built_naming_use_pouryakit_postgres()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            AppFixture.ComposeHost(
                "Development",
                Fixture.ConnectionString,
                new RecordedLogs(),
                builder => builder.AddPouryaKitModules(modules => modules.Add<SubscriptionOnlyModule>())
            )
        );

        Assert.Equal(
            "SubscriptionOnlyModule has the subscriptions tests.subscription-only but registers no Unit of Work, so each of their deliveries would fail before its handler runs. Declare its data in its Composition Root, for example with UsePouryaKitPostgres.",
            thrown.Message
        );
    }

    // Each needs a durable queue of its own (the final structural review of Wave 2, finding 11: a check at the build).
    [Fact]
    public void A_delayed_command_with_a_subscriptions_name_fails_the_host_when_built()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            AppFixture.ComposeHost(
                "Development",
                Fixture.ConnectionString,
                new RecordedLogs(),
                builder => builder.Services.AddPouryaKitDelayedCommand<WriteNoteCommand>("Stub.Note-Shared", typeof(StubModule))
            )
        );

        Assert.Equal(
            $"The delayed command {typeof(WriteNoteCommand).FullName} and a subscription have the name stub.note-shared, ignoring case; each needs a queue of its own.",
            thrown.Message
        );
    }

    // A Module counts as composed when it is added, whatever its Composition Root registers (#61 review finding F4).
    [Fact]
    public void A_declaration_without_its_module_fails_the_build_of_a_host_whose_modules_make_no_kit_registration()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.AddPouryaKitModules(modules => modules.Add<QueryOnlyModule>());
        builder.Services.Publish<UnownedV1>("tests.unowned.v1");

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        // Beside the Outbox the Host lacks (#62), one line per problem.
        Assert.Contains(Unowned["Publish"].Problem, thrown.Message.Split(Environment.NewLine));
    }

    [Fact]
    public void A_declaration_for_a_module_the_host_never_composed_fails_the_host_when_built()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.AddPouryaKitModules(modules => modules.Add<QueryOnlyModule>());
        builder.Services.Publish<UnownedV1>("tests.unowned.v1", typeof(BillingModule));

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        // Beside the Outbox the Host lacks (#62), one line per problem.
        Assert.Contains(
            $"The Integration Event {typeof(UnownedV1).FullName} is declared for BillingModule, which this Host does not compose, so it runs in no Module of the Host and publishing it would fail once the Host serves. Add BillingModule with AddPouryaKitModules, or name a Module the Host composes.",
            thrown.Message.Split(Environment.NewLine)
        );
    }

    // In a Host without Modules the line also offers the application's form, without a Module (#61 review finding F9).
    [Fact]
    public void A_declaration_naming_a_module_in_a_host_without_modules_fails_the_host_when_built_offering_the_form_without_one()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.Services.Publish<UnownedV1>("tests.unowned.v1", typeof(BillingModule));
        builder.AddPouryaKitCompositionChecks();

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        // Beside the Outbox the Host lacks (#62), one line per problem.
        Assert.Contains(
            $"The Integration Event {typeof(UnownedV1).FullName} is declared for BillingModule, which this Host does not compose, so it runs in no Module of the Host and publishing it would fail once the Host serves. Add BillingModule with AddPouryaKitModules, or declare it for the application, without a Module, with Publish<UnownedV1>(wireName).",
            thrown.Message.Split(Environment.NewLine)
        );
    }

    // AddPouryaKitTenancy covers only the assemblies it names (ticket #145): the Module's Cqrs scan of a second assembly
    // would dispatch that assembly's command without a tenant, though another authorizer of it is registered.
    [Fact]
    public void A_tenant_scoped_module_that_dispatches_an_operation_its_tenancy_declaration_did_not_name_fails_the_host_when_built()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            ["--environment", "Production", $"--ConnectionStrings:app={Fixture.ConnectionString}"]
        );
        builder.AddPouryaKitModules(modules => modules.Add<PartlyTenantScopedModule>());

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(
            $"PartlyTenantScopedModule is tenant-scoped, but it dispatches {typeof(UnownedCommand).FullName} without the tenant requirement, so it would run without a tenant. Name its assembly ModularMonolith.Tests.Unowned to AddPouryaKitTenancy in its Composition Root.",
            thrown.Message
        );
    }

    [Fact]
    public void A_tenant_scoped_application_that_dispatches_an_operation_its_tenancy_declaration_did_not_name_fails_the_host_when_built()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.Services.AddPouryaKitTenancy(typeof(UnversionedEndpoints).Assembly);
        builder.Services.AddPouryaKitCqrs(typeof(UnversionedEndpoints).Assembly, typeof(UnownedCommand).Assembly);
        builder.AddPouryaKitCompositionChecks();

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        // Beside the Unit of Work the Host lacks, one line per problem.
        Assert.Contains(
            $"The application is tenant-scoped, but it dispatches {typeof(UnownedCommand).FullName} without the tenant requirement, so it would run without a tenant. Name its assembly ModularMonolith.Tests.Unowned to AddPouryaKitTenancy in the Host's own services.",
            thrown.Message.Split(Environment.NewLine)
        );
    }

    // A recurring command runs as a system Actor with no tenant (ticket #117); a delayed command, which runs with the
    // tenant of the Operation that scheduled it, is not refused beside it.
    [Fact]
    public void A_recurring_command_in_a_tenant_scoped_module_fails_the_host_when_built_naming_the_module()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            ["--environment", "Production", $"--ConnectionStrings:app={Fixture.ConnectionString}"]
        );
        builder.UsePouryaKitWolverine("app");
        builder.AddPouryaKitModules(modules => modules.Add<TenantScopedSchedulingModule>());

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(
            $"The recurring command tests.tenant-recurring, {typeof(WriteNoteCommand).FullName}, is declared in TenantScopedSchedulingModule, which is tenant-scoped, but its occurrences run for the system Actor tests.recurring with no tenant, and the Kit runs no recurring command per tenant. Declare it in a Module that is not tenant-scoped.",
            thrown.Message
        );
    }

    // A Snapshot's identity and version hold the aggregate's key and no tenant (ticket #117): a shelf's key, two codes,
    // is refused; a folder's, one Guid, and a docket's, which includes its tenant, are not.
    [Fact]
    public void A_snapshot_in_a_tenant_scoped_module_of_an_aggregate_whose_key_two_tenants_could_share_fails_the_host_when_built()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            ["--environment", "Production", $"--ConnectionStrings:app={Fixture.ConnectionString}"]
        );
        builder.UsePouryaKitWolverine("app");
        builder.AddPouryaKitModules(modules => modules.Add<TenantScopedSnapshotsModule>());

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(
            $"TenantScopedSnapshotsModule is tenant-scoped, but it publishes the Snapshot of {typeof(Shelf).FullName}, whose key (Aisle, Bay) neither includes the tenant nor is a Guid, so two tenants' aggregates could share a Snapshot's identity and version, and a receiver would apply only one of them. Key Shelf by a Guid the application sets, such as a Guid version 7, or include its tenant in the key.",
            thrown.Message
        );
    }

    // As at commit, a declaration reaches only its own Module's aggregates (#117 review F1): a tenant-scoped Module's
    // Snapshot of every aggregate is judged by Catalog's, keyed by Guids, in its own DbContext, never by the Stub's,
    // keyed by a long, by strings or by a time, in another Module's.
    [Fact]
    public void A_snapshot_in_a_tenant_scoped_module_is_judged_by_the_aggregates_of_its_own_db_context_only()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            ["--environment", "Production", $"--ConnectionStrings:app={Fixture.ConnectionString}"]
        );
        builder.UsePouryaKitWolverine("app");
        builder.AddPouryaKitModules(modules =>
        {
            modules.Add<TenantScopedEveryAggregateModule>();
            modules.Add<StubDataModule>();
        });

        using var host = builder.Build();

        Assert.True(host.Services.GetRequiredService<HostComposition>().IsTenantScoped(typeof(TenantScopedEveryAggregateModule)));
    }

    // AddPouryaKitModules registers what a Module's Operation needs, the Domain Event drain its Unit of Work resolves
    // included, even when no Composition Root calls AddPouryaKitCqrs (#61 review finding F2).
    [Fact]
    public async Task A_module_that_registers_its_handler_by_hand_commits_a_command_through_its_gateway()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            ["--environment", "Production", $"--ConnectionStrings:app={Fixture.ConnectionString}"]
        );
        builder.AddPouryaKitModules(modules => modules.Add<HandRegisteredModule>());
        using var host = builder.Build();

        var written = await host
            .Services.GetRequiredService<IGatewayProcessor<HandRegisteredModule>>()
            .SendAsync(new WriteNoteCommand("by hand"), new OperationContext(GatewayTestExtensions.TestActor), TestContext.Current.CancellationToken);

        Assert.True(written.IsSuccess);
        Assert.Equal(1L, await Fixture.ScalarAsync<long>("select count(*) from stub.notes where text = 'by hand'"));
    }

    // What the checks of ticket #62 read: each Module the Host composed, with the Feature assemblies its Composition Root
    // scanned, inferred from its definition here, and none for the application's own services.
    [Fact]
    public void The_host_composition_names_each_module_with_the_feature_assembly_its_builder_inferred()
    {
        var composition = Fixture.Host.Services.GetRequiredService<HostComposition>();

        Assert.Equal([typeof(CatalogModule), typeof(InventoryModule), typeof(BillingModule), typeof(StubModule)], composition.Modules);
        Assert.All(composition.Modules, module => Assert.Equal([module.Assembly], composition.FeatureAssembliesOf(module)));
        Assert.Empty(composition.FeatureAssembliesOf(null));
    }
}

/// <summary>An Integration Event no Module publishes.</summary>
internal sealed record UnownedV1(Guid Id);

// Resolves two singletons from the built container, which then owns their disposal, and throws.
internal sealed class ThrowingCheck : ICompositionCheck
{
    public const string Problem = "The check itself failed.";

    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        services.GetRequiredService<FailingDisposal>();
        services.GetRequiredService<DisposalProbe>();
        throw new InvalidOperationException(Problem);
    }
}

// Resolves the given singletons in order, so the container disposes the last first, then the probe unless it was
// given, and answers with two problems.
internal sealed class ReportingCheck(params Type[] resolved) : ICompositionCheck
{
    public static readonly string[] Problems = ["The first problem, with its fix.", "The second problem, with its fix."];

    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        foreach (var service in resolved)
        {
            services.GetRequiredService(service);
        }

        services.GetRequiredService<DisposalProbe>();
        return Problems;
    }
}

internal sealed class DisposalProbe : IDisposable
{
    public bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;
}

internal sealed class FailingDisposal : IDisposable
{
    public void Dispose() => throw new NotSupportedException("The disposal failed.");
}

// The container's Dispose throws for a service it owns that is only IAsyncDisposable; its DisposeAsync disposes it.
internal sealed class AsyncOnlyDisposal : IAsyncDisposable
{
    public bool Disposed { get; private set; }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

// Its disposal awaits, so its continuation goes to the synchronization context that was current when it began.
internal sealed class AwaitingDisposal : IAsyncDisposable
{
    public bool Disposed { get; private set; }

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        Disposed = true;
    }
}

// The context of a thread that is blocked: what is posted to it never runs.
internal sealed class BlockedContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) { }
}

// Its data and its one handler, registered by hand: no Composition Root of its Host calls AddPouryaKitCqrs.
public sealed class HandRegisteredModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module)
    {
        module.UsePouryaKitPostgres<StubDbContext>(connectionName: "app", schema: "stub");
        module.AddScoped<CommandHandler<WriteNoteCommand>, WriteNoteCommandHandler>();
    }
}

// Tenant-scoped for one assembly, with Cqrs scanning a second beside it, whose command has an authorizer that is not the
// tenant requirement.
public sealed class PartlyTenantScopedModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module)
    {
        module.AddPouryaKitTenancy(typeof(UnversionedEndpoints).Assembly);
        module.UsePouryaKitPostgres<StubDbContext>(connectionName: "app", schema: "stub");
        module.AddPouryaKitCqrs(typeof(UnversionedEndpoints).Assembly, typeof(UnownedCommand).Assembly);
        module.AddScoped<IAuthorizer<UnownedCommand>, AllowingUnownedCommandAuthorizer>();
    }
}

// Tenant-scoped, with a delayed command beside its recurring one.
public sealed class TenantScopedSchedulingModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module)
    {
        module.AddPouryaKitTenancy(typeof(UnversionedEndpoints).Assembly);
        module.UsePouryaKitPostgres<StubDbContext>(connectionName: "app", schema: "stub");
        module.AddPouryaKitDelayedCommand<WriteDelayedNoteCommand>("stub.delayed-note");
        module.AddPouryaKitRecurringCommand("tests.tenant-recurring", "tests.recurring", at => new WriteNoteCommand($"at {at:O}"));
    }
}

// Tenant-scoped, publishing the Snapshots of three aggregates the Stub's DbContext maps, under the Stub's wire names.
public sealed class TenantScopedSnapshotsModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module)
    {
        module.AddPouryaKitTenancy(typeof(UnversionedEndpoints).Assembly);
        module.UsePouryaKitPostgres<StubDbContext>(connectionName: "app", schema: "stub");
        module.PublishSnapshot<Folder, FolderSnapshotV1, FolderSnapshotMapper>("stub.folder-snapshot.v1");
        module.PublishSnapshot<Docket, DocketSnapshotV1, DocketSnapshotMapper>("stub.docket-snapshot.v1");
        module.PublishSnapshot<Shelf, ShelfSnapshotV1, ShelfSnapshotMapper>("stub.shelf-snapshot.v1");
    }
}

// Tenant-scoped, over Catalog's DbContext, whose aggregates are keyed by Guids, publishing the Snapshot of every
// aggregate.
public sealed class TenantScopedEveryAggregateModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module)
    {
        module.AddPouryaKitTenancy(typeof(UnversionedEndpoints).Assembly);
        module.UsePouryaKitPostgres<CatalogDbContext>(connectionName: "app", schema: "catalog");
        module.PublishSnapshot<IAggregateRoot, AggregateSnapshotV1, AggregateSnapshotMapper>("tests.aggregate-snapshot.v1");
    }
}

// Not tenant-scoped, over the Stub's DbContext, and publishing nothing.
public sealed class StubDataModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module) => module.UsePouryaKitPostgres<StubDbContext>(connectionName: "app", schema: "stub");
}

internal sealed record AggregateSnapshotV1(string Aggregate, long Version);

internal sealed class AggregateSnapshotMapper : ISnapshotMapper<IAggregateRoot, AggregateSnapshotV1>
{
    public AggregateSnapshotV1 Map(IAggregateRoot aggregate) => new(aggregate.GetType().Name, aggregate.Version);

    public AggregateSnapshotV1 Tombstone(IAggregateRoot aggregate) => new(aggregate.GetType().Name, aggregate.Version);
}

internal sealed class AllowingUnownedCommandAuthorizer : IAuthorizer<UnownedCommand>
{
    public Task<Result> AuthorizeAsync(UnownedCommand operation, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
}

// Subscribes to an Integration Event the Stub publishes, and declares no data.
public sealed class SubscriptionOnlyModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module) =>
        module.Subscribe<NoteSharedV1, RecordSharedNoteIntegrationEventHandler>("tests.subscription-only");
}

// Declares, on its own builder, a subscription it names for the Stub Module.
public sealed class OtherModuleNamingModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module) =>
        module.Subscribe<NoteSharedV1, RecordSharedNoteIntegrationEventHandler>("tests.other-module", typeof(StubModule));
}
