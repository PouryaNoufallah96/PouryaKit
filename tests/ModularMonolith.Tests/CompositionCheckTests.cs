using Billing;
using Catalog;
using Catalog.Products.Features.GetProduct;
using Inventory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ModularMonolith.Tests.Misnamed;
using ModularMonolith.Tests.Stub;
using ModularMonolith.Tests.Unowned;
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

    // The other way a check fails the build: it answers with its problems, which outlast a failing disposal too, whether
    // a singleton's Dispose throws or the container's own does, for a singleton that can only be disposed asynchronously.
    [Theory]
    [InlineData(typeof(FailingDisposal))]
    [InlineData(typeof(AsyncOnlyDisposal))]
    public void Composition_problems_fail_the_build_with_every_problem_though_the_container_cannot_be_disposed(Type undisposable)
    {
        var resolved = new DisposalProbe();
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.Services.AddSingleton(undisposable, _ => Activator.CreateInstance(undisposable)!);
        builder.Services.AddSingleton(_ => resolved);
        builder.Services.AddSingleton<ICompositionCheck>(new ReportingCheck(undisposable));
        builder.AddPouryaKitCompositionChecks();

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(string.Join(Environment.NewLine, ReportingCheck.Problems), thrown.Message);
        Assert.True(resolved.Disposed);
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

// Resolves the singleton the container cannot dispose, then the probe, and answers with two problems.
internal sealed class ReportingCheck(Type undisposable) : ICompositionCheck
{
    public static readonly string[] Problems = ["The first problem, with its fix.", "The second problem, with its fix."];

    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        services.GetRequiredService(undisposable);
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

// The container's Dispose throws for a service it owns that is only IAsyncDisposable.
internal sealed class AsyncOnlyDisposal : IAsyncDisposable
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
