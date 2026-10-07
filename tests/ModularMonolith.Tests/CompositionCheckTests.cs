using Billing;
using Catalog;
using Catalog.Products.Features.GetProduct;
using Inventory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ModularMonolith.Tests.Stub;
using ModularMonolith.Tests.Unowned;
using PouryaKit.Cqrs;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

// The composition checks AddPouryaKitCompositionChecks runs when the Host starts, before it serves (ticket #61): a
// Module's declaration made without its Module, a container a later ConfigureContainer replaced, a cacheable query
// without caching, a command without a Unit of Work.
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
    public async Task A_module_declaration_made_without_its_module_in_a_host_of_modules_stops_the_host_at_start(string declaration)
    {
        await using var host = AppFixture.ComposeHost("Development", Fixture.ConnectionString, new RecordedLogs(), builder => Unowned[declaration].Declare(builder.Services));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

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
    public async Task A_cacheable_query_in_a_host_without_caching_stops_the_host_at_start_naming_the_call()
    {
        await using var host = AppFixture.ComposeHost(
            "Development",
            Fixture.ConnectionString,
            new RecordedLogs(),
            builder => builder.Services.RemoveAll<IQueryCache>()
        );

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.StartsWith("The Host registers no query cache, so every cacheable query would run uncached: ", thrown.Message);
        Assert.Contains(typeof(GetProductQuery).FullName!, thrown.Message);
        Assert.Contains(typeof(CountNotesQuery).FullName!, thrown.Message);
        Assert.EndsWith(". Call AddPouryaKitCaching() on the Host's services.", thrown.Message);
    }

    [Fact]
    public async Task A_command_handler_in_a_host_without_a_unit_of_work_stops_the_host_at_start()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.Services.AddScoped<CommandHandler<RelabelInAuthorizationCommand>, RelabelInAuthorizationCommandHandler>();
        builder.AddPouryaKitCompositionChecks();
        using var host = builder.Build();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            $"The Host handles the command {typeof(RelabelInAuthorizationCommand).FullName} but registers no Unit of Work, so the command would fail when it runs. Register the Unit of Work of a persistence package, for example with UsePostgres.",
            thrown.Message
        );
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

// Declares, on its own builder, a subscription it names for the Stub Module.
public sealed class OtherModuleNamingModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module) =>
        module.Subscribe<NoteSharedV1, RecordSharedNoteIntegrationEventHandler>("tests.other-module", typeof(StubModule));
}
