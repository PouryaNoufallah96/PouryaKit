using Billing;
using Catalog;
using Catalog.Products.Features.GetProduct;
using Inventory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ModularMonolith.Tests.Stub;
using ModularMonolith.Tests.Unowned;
using ModularMonolith.Tests.Unversioned;
using PouryaKit.Cqrs;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;
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
            "The Host's container was built by another service provider factory than the Kit's: a ConfigureContainer call after AddPouryaKitCompositionChecks, which AddPouryaKitModules calls, replaced it.",
            thrown.Message
        );
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
            $"The Host handles the command {typeof(UnownedCommand).FullName} but registers no Unit of Work, so the command would fail when it runs. Register the Unit of Work of a persistence package, for example with UsePostgres.",
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
            $"The Host handles the command {typeof(UnownedCommand).FullName} but registers no Unit of Work, so the command would fail when it runs. Register the Unit of Work of a persistence package, for example with UsePostgres.",
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

    // A Module counts as composed when it is added, whatever its Composition Root registers (#61 review finding F4).
    [Fact]
    public void A_declaration_without_its_module_fails_the_build_of_a_host_whose_modules_make_no_kit_registration()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.AddPouryaKitModules(modules => modules.Add<QueryOnlyModule>());
        builder.Services.Publish<UnownedV1>("tests.unowned.v1");

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(Unowned["Publish"].Problem, thrown.Message);
    }

    [Fact]
    public void A_declaration_for_a_module_the_host_never_composed_fails_the_host_when_built()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.AddPouryaKitModules(modules => modules.Add<QueryOnlyModule>());
        builder.Services.Publish<UnownedV1>("tests.unowned.v1", typeof(BillingModule));

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(
            $"The Integration Event {typeof(UnownedV1).FullName} is declared for BillingModule, which this Host does not compose, so it runs in no Module of the Host and publishing it would fail once the Host serves. Add BillingModule with AddPouryaKitModules, or name a Module the Host composes.",
            thrown.Message
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

        Assert.Equal(
            $"The Integration Event {typeof(UnownedV1).FullName} is declared for BillingModule, which this Host does not compose, so it runs in no Module of the Host and publishing it would fail once the Host serves. Add BillingModule with AddPouryaKitModules, or declare it for the application, without a Module, with Publish<UnownedV1>(wireName).",
            thrown.Message
        );
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

// Its data and its one handler, registered by hand: no Composition Root of its Host calls AddPouryaKitCqrs.
public sealed class HandRegisteredModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module)
    {
        module.UsePostgres<StubDbContext>(connectionName: "app", schema: "stub");
        module.AddScoped<CommandHandler<WriteNoteCommand>, WriteNoteCommandHandler>();
    }
}

// Declares, on its own builder, a subscription it names for the Stub Module.
public sealed class OtherModuleNamingModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module) =>
        module.Subscribe<NoteSharedV1, RecordSharedNoteIntegrationEventHandler>("tests.other-module", typeof(StubModule));
}
