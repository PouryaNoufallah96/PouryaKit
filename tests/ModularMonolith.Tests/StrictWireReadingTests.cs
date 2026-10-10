using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Catalog;
using Inventory;
using Inventory.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Testing;
using Wolverine;

namespace ModularMonolith.Tests;

// Two Hosts whose contract types under one wire name differ (#60): the Catalog Host publishes a contract of another
// shape under the wire name Inventory.Host receives as ShapeV1. Inventory.Host reads the body strictly, so a member its
// contract requires that the body lacks, a null its contract forbids, or one of its members named twice (#86), fails the
// read at its broker queue, and the envelope is dead-lettered there with the error before any subscription receives a
// copy. A member the body has and the contract lacks is skipped.
public sealed class StrictWireReadingTests(BrokerFixture broker) : IClassFixture<BrokerFixture>
{
    private const string Subscription = "inventory.shape";
    private static readonly string HostQueue = $"rabbitmq://queue/{InventoryHost.ServiceName}";

    [Theory]
    [InlineData("without the name", "JSON deserialization for type 'ModularMonolith.Tests.ShapeV1' was missing required properties including: 'name'.")]
    [InlineData("with a null name", "The constructor parameter 'Name' on type 'ModularMonolith.Tests.ShapeV1' doesn't allow null values.")]
    public async Task A_body_without_a_member_its_contract_requires_or_with_null_for_one_is_dead_lettered_at_the_host_queue_with_the_error_and_never_handled(
        string body,
        string error
    )
    {
        var environment = await broker.CreateEnvironmentAsync();
        using var inventory = await environment.StartInventoryHostAsync(configure: ReceivesShapes);
        var id = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        (Action<WebApplicationBuilder> Declare, Func<IHost, Task> Publish) publisher = body switch
        {
            "without the name" => (
                builder => builder.Services.Publish<ShapeWithoutNameV1>(ShapeV1.WireName, typeof(CatalogModule)),
                catalog => PublishAsync(catalog, new ShapeWithoutNameV1(id, product), id)
            ),
            _ => (
                builder => builder.Services.Publish<ShapeWithNullableNameV1>(ShapeV1.WireName, typeof(CatalogModule)),
                catalog => PublishAsync(catalog, new ShapeWithNullableNameV1(id, product, null), id)
            ),
        };
        await using var catalog = await environment.StartCatalogHostAsync(publisher.Declare);

        await publisher.Publish(catalog);

        await AssertDeadLetteredAsync(environment, id, error);
    }

    // The publishing contract writes ShapeV1's name a second time, capitalised, from its extension data.
    [Fact]
    public async Task A_body_that_names_a_member_of_its_contract_twice_is_dead_lettered_at_the_host_queue_with_the_error_and_never_handled()
    {
        var environment = await broker.CreateEnvironmentAsync();
        using var inventory = await environment.StartInventoryHostAsync(configure: ReceivesShapes);
        await using var catalog = await environment.StartCatalogHostAsync(builder =>
            builder.Services.Publish<ShapeWithNameTwiceV1>(ShapeV1.WireName, typeof(CatalogModule))
        );
        var sent = new ShapeWithNameTwiceV1(Guid.CreateVersion7(), Guid.CreateVersion7(), "Lamp") { Again = new() { ["Name"] = "Shade" } };

        await PublishAsync(catalog, sent, sent.Id);

        await AssertDeadLetteredAsync(
            environment,
            sent.Id,
            $"Duplicate property 'name' encountered during deserialization of type '{typeof(ShapeV1).FullName}'."
        );
    }

    [Fact]
    public async Task A_body_with_a_member_its_contract_lacks_is_handled_with_the_members_it_has()
    {
        var environment = await broker.CreateEnvironmentAsync();
        using var inventory = await environment.StartInventoryHostAsync(configure: ReceivesShapes);
        await using var catalog = await environment.StartCatalogHostAsync(builder =>
            builder.Services.Publish<ShapeWithColourV1>(ShapeV1.WireName, typeof(CatalogModule))
        );
        var sent = new ShapeWithColourV1(Guid.CreateVersion7(), Guid.CreateVersion7(), "Lamp", "red");

        await PublishAsync(catalog, sent, sent.Id);

        await BrokerEnvironment.WaitUntilAsync(
            environment.Inventory,
            $"exists (select from inventory.pouryakit_receipts where subscription = '{Subscription}' and message_id = '{sent.Id}')",
            "Inventory.Host never applied the body with a member its contract lacks."
        );
        Assert.Equal(new ShapeV1(sent.Id, sent.ProductId, "Lamp"), Assert.Single(ShapeIntegrationEventHandler.Received, received => received.Id == sent.Id));
        Assert.Empty(await BrokerEnvironment.RowsAsync(environment.Inventory, "select id from wolverine.wolverine_dead_letters"));
    }

    private static void ReceivesShapes(WebApplicationBuilder builder)
    {
        builder.Services.Receive<ShapeV1>(ShapeV1.WireName);
        builder.Services.Subscribe<ShapeV1, ShapeIntegrationEventHandler>(Subscription, typeof(InventoryModule));
    }

    // Straight through Wolverine, with the headers the Kit's Outbox writes, as a publishing Module's would be.
    private static async Task PublishAsync<TContract>(IHost catalog, TContract contract, Guid id)
        where TContract : class
    {
        await using var scope = catalog.Services.CreateAsyncScope();
        var options = new DeliveryOptions().WithOperationContext(new OperationContext(GatewayTestExtensions.TestActor));
        options.Headers[IntegrationEventHeaders.DomainEventId] = id.ToString();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().PublishAsync(contract, options);
    }

    private static async Task AssertDeadLetteredAsync(BrokerEnvironment environment, Guid id, string error)
    {
        await ArrivedAsync(environment);

        Assert.DoesNotContain(ShapeIntegrationEventHandler.Received, received => received.Id == id);
        var deadLetter = Assert.Single(
            await BrokerEnvironment.RowsAsync(environment.Inventory, "select received_at, message_type, exception_type, exception_message from wolverine.wolverine_dead_letters")
        );
        Assert.Equal([HostQueue, ShapeV1.WireName, "System.Text.Json.JsonException"], deadLetter[..3]);
        Assert.StartsWith(error, (string)deadLetter[3], StringComparison.Ordinal);
        // No subscription received a copy, whatever its handler would have done with it.
        Assert.Equal(
            0L,
            await BrokerEnvironment.ScalarAsync<long>(environment.Inventory, $"select count(*) from wolverine.wolverine_incoming_envelopes where received_at = 'local://{Subscription}/'")
        );
    }

    // The broker envelope reached Inventory.Host's store, and everything it caused there is settled.
    private static async Task ArrivedAsync(BrokerEnvironment environment)
    {
        await BrokerEnvironment.WaitUntilAsync(
            environment.Inventory,
            $"exists (select from wolverine.wolverine_incoming_envelopes where received_at = '{HostQueue}' and message_type = '{ShapeV1.WireName}') or exists (select from wolverine.wolverine_dead_letters)",
            "The envelope never reached Inventory.Host."
        );
        await BrokerEnvironment.WaitUntilSettledAsync(environment.Inventory);
    }
}

// Inventory.Host's contract under the wire name, and four publishing Hosts' contracts of other shapes under it.
internal sealed record ShapeV1(Guid Id, Guid ProductId, string Name)
{
    public const string WireName = "tests.shape.v1";
}

internal sealed record ShapeWithoutNameV1(Guid Id, Guid ProductId);

internal sealed record ShapeWithNullableNameV1(Guid Id, Guid ProductId, string? Name);

internal sealed record ShapeWithColourV1(Guid Id, Guid ProductId, string Name, string Colour);

// System.Text.Json writes extension data after the members, without checking their names.
internal sealed record ShapeWithNameTwiceV1(Guid Id, Guid ProductId, string Name)
{
    [JsonExtensionData]
    public Dictionary<string, object>? Again { get; init; }
}

internal sealed class ShapeIntegrationEventHandler : IIntegrationEventHandler<ShapeV1>
{
    private static readonly ConcurrentQueue<ShapeV1> Applied = [];

    public static IReadOnlyList<ShapeV1> Received => [.. Applied];

    public Task<Result> Handle(ShapeV1 integrationEvent, CancellationToken cancellationToken)
    {
        Applied.Enqueue(integrationEvent);
        return Task.FromResult(Result.Success());
    }
}
