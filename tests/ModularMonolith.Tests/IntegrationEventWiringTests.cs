using System.Reflection;
using Billing;
using Catalog;
using Catalog.Contracts.Products.Events.V1;
using Inventory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Messaging.Wolverine.RabbitMq;
using Wolverine.Util;

namespace ModularMonolith.Tests;

public sealed class IntegrationEventWiringTests
{
    private const string ReservedRule =
        "is not a valid name: use letters and digits in segments joined by single dots or hyphens, not beginning with amq., which RabbitMQ reserves.";

    private static readonly Dictionary<string, (Action<IServiceCollection> Declare, string Problem)> Mistakes = new()
    {
        ["no wire name"] = (s => s.Publish<TwinV1>(""), $"{typeof(TwinV1).FullName}'s wire name is missing"),
        ["an invalid wire name"] = (s => s.Publish<TwinV1>("tests.twin v1"), "wire name tests.twin v1 is not a valid name"),
        // RabbitMQ refuses to declare an exchange or queue under the prefix: the Kit refuses it here instead.
        ["a wire name RabbitMQ reserves"] = (s => s.Publish<TwinV1>("amq.twin.v1"), $"wire name amq.twin.v1 {ReservedRule}"),
        ["a received wire name RabbitMQ reserves, in capitals"] = (s => s.Receive<TwinV1>("AMQ.Twin.V1"), $"wire name AMQ.Twin.V1 {ReservedRule}"),
        ["a subscription name RabbitMQ reserves"] = (
            s => s.Subscribe<TwinV1, TwinIntegrationEventHandler>("amq.twin"),
            $"subscription name amq.twin {ReservedRule}"
        ),
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
            "subscription name tests.twin/one is not a valid name"
        ),
        ["a subscription name twice"] = (
            s =>
                s.Subscribe<TwinV1, TwinIntegrationEventHandler>("tests.twin")
                    .Subscribe<OtherTwinV1, OtherTwinIntegrationEventHandler>("Tests.Twin"),
            "have the subscription name tests.twin, ignoring case"
        ),
        ["no handler"] = (
            s => s.Subscribe<TwinV1, AbstractTwinIntegrationEventHandler>("tests.twin"),
            "is abstract, so the subscription tests.twin has no handler to run"
        ),
        ["a subscribed Domain Event"] = (
            s => s.Subscribe<NoteToShareDomainEvent, NoteToShareIntegrationEventHandler>("tests.note-shared"),
            "subscribes to the Domain Event"
        ),
        ["a received type the Host publishes"] = (
            s => s.Publish<TwinV1>("tests.twin.v1").Receive<TwinV1>("tests.twin.v1"),
            $"{typeof(TwinV1).FullName} is published by the application in this Host and declared as received from another Host"
        ),
        ["a published type the Host receives"] = (
            s => s.Receive<TwinV1>("tests.twin.v1").Publish<TwinV1>("tests.twin.v1"),
            $"{typeof(TwinV1).FullName} is published by the application in this Host and declared as received from another Host"
        ),
        ["one received type as a Snapshot and an Integration Event"] = (
            s => s.Receive<TwinV1>("tests.twin.v1").ReceiveSnapshot<TwinV1>("tests.twin.v1"),
            $"{typeof(TwinV1).FullName} is received as the Integration Event tests.twin.v1 and as the Snapshot tests.twin.v1"
        ),
        ["a received Domain Event"] = (s => s.Receive<NoteToShareDomainEvent>("tests.note-shared"), "is a Domain Event"),
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
    public void A_service_name_rabbitmq_reserves_is_refused_when_the_host_joins_the_broker()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--ConnectionStrings:broker=amqp://localhost/"]);

        var thrown = Assert.Throws<ArgumentException>(() => builder.UsePouryaKitRabbitMq("broker", "amq.inventory"));

        Assert.StartsWith(
            "The service name amq.inventory names the Host's queue on the broker; use letters and digits in segments joined by single dots or hyphens, not beginning with amq., which RabbitMQ reserves.",
            thrown.Message
        );
    }

    // A variable set to nothing gives a blank value, not a missing one. Wolverine applies nothing of it and says
    // nothing, so the Host would dial the client's default broker.
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void A_blank_broker_connection_string_is_refused_when_the_host_joins_the_broker_naming_the_connection(string blank)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection([new("ConnectionStrings:broker", blank)]);

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.UsePouryaKitRabbitMq("broker", "inventory"));

        Assert.Equal(
            "The configuration has no connection string named broker for the RabbitMQ broker, or leaves it blank. Set ConnectionStrings:broker.",
            thrown.Message
        );
    }

    [Fact]
    public void Building_fails_when_modules_publish_integration_events_and_the_host_has_no_outbox()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.UsePouryaKitWolverine();
        builder.Services.Publish<TwinV1>("tests.twin.v1");

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.StartsWith("Notification Handlers, Integration Events, Snapshots or delayed commands are declared, but nothing writes them to an Outbox", thrown.Message);
    }

    [Fact]
    public void Building_fails_on_a_received_integration_event_wolverine_named_before_it_was_declared_and_says_it_is_received()
    {
        var named = typeof(EarlyReceivedV1).ToMessageTypeName();
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.UsePouryaKitWolverine();
        // An Outbox, as a Host that subscribes registers one, so the Outbox check stays quiet.
        builder.Services.AddSingleton<INotificationOutbox, StandInOutbox>();
        builder.Services.Receive<EarlyReceivedV1>("tests.early-received.v1");
        builder.Services.Subscribe<EarlyReceivedV1, EarlyReceivedIntegrationEventHandler>("tests.early-received");

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        // Beside the Unit of Work the application lacks, one line per problem.
        Assert.Contains(
            $"{typeof(EarlyReceivedV1).FullName} is received as tests.early-received.v1, but Wolverine named it {named} before it was declared and would send it under that name. Declare it before anything in the process sends or names it.",
            thrown.Message.Split(Environment.NewLine)
        );
    }

    // A received type binds the Host's queue only through a subscription to it (Subscriptions' IWireNames.Received).
    [Theory]
    [InlineData("Receive")]
    [InlineData("ReceiveSnapshot")]
    public void Building_fails_on_a_type_received_from_another_host_that_no_subscription_handles_naming_subscribe(string declaration)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.UsePouryaKitWolverine();
        if (declaration == "Receive")
        {
            builder.Services.Receive<UnsubscribedV1>("tests.unsubscribed.v1");
        }
        else
        {
            builder.Services.ReceiveSnapshot<UnsubscribedV1>("tests.unsubscribed.v1");
        }

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(
            $"{typeof(UnsubscribedV1).FullName} is declared received from another Host as tests.unsubscribed.v1, but no subscription of this Host handles it, so nothing would receive it. Subscribe<UnsubscribedV1, THandler>(subscriptionName) in the Composition Root of the Module that receives it, or remove the {declaration} declaration.",
            thrown.Message
        );
    }

    [Fact]
    public void Inventory_and_billing_know_catalog_only_through_its_contracts_and_no_contract_or_module_references_a_transport()
    {
        var inventory = ReferencedBy(typeof(InventoryModule).Assembly);
        Assert.Contains("Catalog.Contracts", inventory);
        Assert.DoesNotContain("Catalog", inventory);
        var billing = ReferencedBy(typeof(BillingModule).Assembly);
        Assert.Contains("Catalog.Contracts", billing);
        Assert.DoesNotContain("Catalog", billing);
        Assert.DoesNotContain("Inventory", billing);
        Assert.DoesNotContain("Billing", inventory);
        // Contracts are plain records: nothing of the Kit, nothing of a transport.
        Assert.DoesNotContain(ReferencedBy(typeof(ProductCreatedV1).Assembly), name => name.StartsWith("PouryaKit", StringComparison.Ordinal));
        Assert.All(
            ReferencedBy(typeof(IIntegrationEventPublisher).Assembly),
            name => Assert.True(name == "PouryaKit.Domain" || name.StartsWith("System", StringComparison.Ordinal), name)
        );
        Assert.All(
            [typeof(CatalogModule).Assembly, typeof(InventoryModule).Assembly, typeof(BillingModule).Assembly],
            module => Assert.DoesNotContain(ReferencedBy(module), name => name.StartsWith("Wolverine", StringComparison.Ordinal))
        );
    }

    private static List<string> ReferencedBy(Assembly assembly) => [.. assembly.GetReferencedAssemblies().Select(name => name.Name!)];
}

// Named for the process by one test only, before it is declared.
internal sealed record EarlyReceivedV1(Guid Id);

internal sealed class EarlyReceivedIntegrationEventHandler : IIntegrationEventHandler<EarlyReceivedV1>
{
    public Task<Result> Handle(EarlyReceivedV1 integrationEvent, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
}

// Received by one test only, which no subscription handles.
internal sealed record UnsubscribedV1(Guid Id);
