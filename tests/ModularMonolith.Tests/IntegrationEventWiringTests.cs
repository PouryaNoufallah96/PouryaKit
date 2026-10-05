using System.Reflection;
using Catalog;
using Catalog.Contracts.Products.Events.V1;
using Inventory;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;

namespace ModularMonolith.Tests;

public sealed class IntegrationEventWiringTests
{
    private static readonly Dictionary<string, (Action<IServiceCollection> Declare, string Problem)> Mistakes = new()
    {
        ["no wire name"] = (s => s.Publish<TwinV1>(""), $"{typeof(TwinV1).FullName}'s wire name is missing"),
        ["an invalid wire name"] = (s => s.Publish<TwinV1>("tests.twin v1"), "wire name tests.twin v1 is not valid as a queue name"),
        ["a wire name twice"] = (
            s => s.Publish<TwinV1>("tests.twin.v1").Publish<OtherTwinV1>("Tests.Twin.V1"),
            "have the wire name Tests.Twin.V1, ignoring case"
        ),
        ["a Domain Event's wire name"] = (
            s =>
                s.AddPouryaKitNotificationHandlers(typeof(StubModule).Assembly)
                    .Publish<TwinV1>("Modular-Monolith.Tests.Note-To-Share"),
            "have the wire name Modular-Monolith.Tests.Note-To-Share, ignoring case"
        ),
        ["a Notification Handler's subscription name"] = (
            s =>
                s.AddPouryaKitNotificationHandlers(typeof(StubModule).Assembly)
                    .Subscribe<TwinV1, TwinIntegrationEventHandler>("modular-monolith.tests.note-to-share.share-note"),
            "have the subscription name modular-monolith.tests.note-to-share.share-note, ignoring case"
        ),
        ["one type under two wire names"] = (
            s => s.Publish<TwinV1>("tests.twin.v1").Publish<TwinV1>("tests.twin.v2"),
            $"{typeof(TwinV1).FullName} is published as tests.twin.v1 by the application and as tests.twin.v2"
        ),
        ["a published Domain Event"] = (s => s.Publish<NoteToShareDomainEvent>("tests.note-shared"), "is a Domain Event"),
        ["no subscription name"] = (
            s => s.Subscribe<TwinV1, TwinIntegrationEventHandler>(""),
            $"{typeof(TwinIntegrationEventHandler).FullName}'s subscription name is missing"
        ),
        ["an invalid subscription name"] = (
            s => s.Subscribe<TwinV1, TwinIntegrationEventHandler>("tests.twin/one"),
            "subscription name tests.twin/one is not valid as a queue name"
        ),
        ["a subscription name twice"] = (
            s =>
                s.Subscribe<TwinV1, TwinIntegrationEventHandler>("tests.twin")
                    .Subscribe<OtherTwinV1, OtherTwinIntegrationEventHandler>("Tests.Twin"),
            "have the subscription name Tests.Twin, ignoring case"
        ),
        ["no handler"] = (
            s => s.Subscribe<TwinV1, AbstractTwinIntegrationEventHandler>("tests.twin"),
            "is abstract, so the subscription tests.twin has no handler to run"
        ),
        ["a subscribed Domain Event"] = (
            s => s.Subscribe<NoteToShareDomainEvent, NoteToShareDomainEventSubscriber>("tests.note-shared"),
            "subscribes to the Domain Event"
        ),
    };

    public static TheoryData<string> DeclarationMistakes() => [.. Mistakes.Keys];

    [Theory]
    [MemberData(nameof(DeclarationMistakes))]
    public void A_composition_root_fails_on(string mistake)
    {
        var (declare, problem) = Mistakes[mistake];

        var thrown = Assert.Throws<InvalidOperationException>(() => declare(new ServiceCollection()));

        Assert.Contains(problem, thrown.Message);
    }

    [Fact]
    public async Task Startup_fails_when_modules_publish_integration_events_and_the_host_has_no_outbox()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.UsePouryaKitWolverine();
        builder.Services.Publish<TwinV1>("tests.twin.v1");
        using var host = builder.Build();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.StartsWith("Modules of this Host publish Integration Events, but it has no Outbox", thrown.Message);
    }

    [Fact]
    public void Inventory_knows_catalog_only_through_its_contracts_and_no_contract_or_module_references_a_transport()
    {
        var inventory = ReferencedBy(typeof(InventoryModule).Assembly);
        Assert.Contains("Catalog.Contracts", inventory);
        Assert.DoesNotContain("Catalog", inventory);
        // Contracts are plain records: nothing of the Kit, nothing of a transport.
        Assert.DoesNotContain(ReferencedBy(typeof(ProductCreatedV1).Assembly), name => name.StartsWith("PouryaKit", StringComparison.Ordinal));
        Assert.All(
            ReferencedBy(typeof(IIntegrationEventPublisher).Assembly),
            name => Assert.True(name == "PouryaKit.Domain" || name.StartsWith("System", StringComparison.Ordinal), name)
        );
        Assert.All(
            [typeof(CatalogModule).Assembly, typeof(InventoryModule).Assembly],
            module => Assert.DoesNotContain(ReferencedBy(module), name => name.StartsWith("Wolverine", StringComparison.Ordinal))
        );
    }

    private static List<string> ReferencedBy(Assembly assembly) => [.. assembly.GetReferencedAssemblies().Select(name => name.Name!)];
}
