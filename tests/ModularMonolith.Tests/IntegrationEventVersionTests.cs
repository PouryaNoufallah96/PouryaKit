using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Events.DomainEvents;
using Catalog.Products.Features.CreateProduct;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

/// <summary>
/// Catalog publishes two versions of one fact during a transition, <c>ProductCreatedV1</c> and <c>ProductCreatedV2</c>,
/// each a type of its own under a wire name of its own; each subscription receives only the version it declared.
/// </summary>
public sealed class IntegrationEventVersionTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private const string V1 = "catalog.product-created.v1";
    private const string V2 = "catalog.product-created.v2";
    private const string StockItems = "inventory.product-created";
    private const string PriceLists = "billing.product-created";
    private const string ProductCategories = "inventory.product-categories";

    [Fact]
    public async Task One_operation_publishes_both_versions_and_each_subscription_applies_only_its_own_with_its_receipt_in_one_commit()
    {
        var category = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        var catalog = Fixture.Gateway<CatalogModule>();
        await catalog.SendAsync(new CreateCategoryCommand(category, $"Lamps {category}"), TestContext.Current.CancellationToken);
        List<object> written = [];
        IReadOnlyList<Delivery> deliveries;
        using (DeliveryProbes.RecordWritten(written))
        {
            (_, deliveries) = await Fixture.WaitForDeliveriesAsync(
                () =>
                    catalog.SendAsync(
                        new CreateProductCommand(product, category, $"Lamp {product}"),
                        new OperationContext(Actor.Person("ada")),
                        TestContext.Current.CancellationToken
                    ),
                TestContext.Current.CancellationToken
            );
        }

        var created = Assert.Single(written.OfType<ProductCreatedDomainEvent>()).Id;
        // v1 reaches its two subscriptions only, v2 its one only: in what was delivered and in what the store holds.
        Assert.Equal(
            [new Delivery(PriceLists, V1, false), new Delivery(ProductCategories, V2, false), new Delivery(StockItems, V1, false)],
            deliveries.Where(delivery => delivery.WireName is V1 or V2).OrderBy(delivery => delivery.Subscription, StringComparer.Ordinal)
        );
        Assert.Equal(
            [[$"local://{PriceLists}/", V1], [$"local://{ProductCategories}/", V2], [$"local://{StockItems}/", V1]],
            await Fixture.RowsAsync(
                $"select received_at, message_type from wolverine.wolverine_incoming_envelopes where message_type in ('{V1}', '{V2}') order by received_at"
            )
        );
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from wolverine.wolverine_dead_letters"));
        // Catalog recorded one publication of each version, both following from the one Domain Event.
        Assert.Equal(
            [[$"{V1}#published", created], [$"{V2}#published", created]],
            await Fixture.RowsAsync("select subscription, message_id from catalog.pouryakit_receipts where subscription like '%#published' order by 1")
        );
        // Each effect, by the Actor who created the product, commits with its subscription's Receipt, keyed by the
        // same identity: the two versions share it, and Inventory's two subscriptions keep a Receipt each.
        Assert.Equal(
            [[product, "ada", StockItems, created]],
            await Fixture.RowsAsync(
                $"""
                select s.product_id, s.created_by, r.subscription, r.message_id from inventory.stock_items s
                join inventory.pouryakit_receipts r on r.subscription = '{StockItems}' and pg_xact_commit_timestamp(r.xmin) = pg_xact_commit_timestamp(s.xmin)
                """
            )
        );
        Assert.Equal(
            [[product, category, "ada", ProductCategories, created]],
            await Fixture.RowsAsync(
                $"""
                select c.product_id, c.category_id, c.filed_by, r.subscription, r.message_id from inventory.product_categories c
                join inventory.pouryakit_receipts r on r.subscription = '{ProductCategories}' and pg_xact_commit_timestamp(r.xmin) = pg_xact_commit_timestamp(c.xmin)
                """
            )
        );
        Assert.Equal(
            [[product, "ada", PriceLists, created]],
            await Fixture.RowsAsync(
                $"""
                select p.product_id, p.created_by, r.subscription, r.message_id from billing.price_lists p
                join billing.pouryakit_receipts r on r.subscription = '{PriceLists}' and pg_xact_commit_timestamp(r.xmin) = pg_xact_commit_timestamp(p.xmin)
                """
            )
        );
    }

    [Fact]
    public async Task Startup_fails_when_wolverine_would_send_one_message_type_wherever_another_published_type_goes()
    {
        // A Host that publishes both and subscribes to neither: a broker between Hosts routes every publication.
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.UsePouryaKitWolverine();
        // An Outbox, as a Host that publishes registers one, so the Host builds and Wolverine's own refusal is what fails.
        builder.Services.AddSingleton<INotificationOutbox, StandInOutbox>();
        builder.Services.Publish<ProducedV1>("tests.produced.v1");
        builder.Services.Publish<MisproducedV2>(typeof(ProducedV1).FullName!);
        using var host = builder.Build();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            $"{typeof(MisproducedV2).FullName}, under the wire name {typeof(ProducedV1).FullName}, would also be sent wherever {typeof(ProducedV1).FullName} is published to, such as its exchange on a broker: Wolverine routes a published type to every message whose wire name, name or full name equals the type's full name, ignoring case. Give it a wire name and a name of its own.",
            thrown.Message
        );
    }

    [Fact]
    public async Task Startup_fails_when_wolverine_would_route_one_message_type_to_the_subscriptions_of_another()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.UsePouryaKitWolverine();
        // An Outbox, as a Host that publishes registers one, so the Host builds and Wolverine's own refusal is what fails.
        builder.Services.AddSingleton<INotificationOutbox, StandInOutbox>();
        builder.Services.Publish<RoutedV1>("tests.routed.v1");
        builder.Services.Subscribe<RoutedV1, RoutedIntegrationEventHandler>("tests.routed");
        // A v2 published under the v1 contract's full name, a valid wire name of its own.
        builder.Services.Publish<MisroutedV2>(typeof(RoutedV1).FullName!);
        using var host = builder.Build();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            $"{typeof(MisroutedV2).FullName}, under the wire name {typeof(RoutedV1).FullName}, would also reach every subscription of {typeof(RoutedV1).FullName}: Wolverine routes a subscribed type to every message whose wire name, name or full name equals the type's full name, ignoring case. Give it a wire name and a name of its own.",
            thrown.Message
        );
    }
}

// Named for the process by one test only, which refuses them before they are declared.
internal sealed record RoutedV1(Guid Id);

// Marks a Host as one whose Unit of Work writes an Outbox; nothing here commits.
internal sealed class StandInOutbox : INotificationOutbox;

internal sealed record ProducedV1(Guid Id);

internal sealed record MisproducedV2(Guid Id, string Note);

internal sealed record MisroutedV2(Guid Id, string Note);

internal sealed class RoutedIntegrationEventHandler : IIntegrationEventHandler<RoutedV1>
{
    public Task<Result> Handle(RoutedV1 integrationEvent, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
}
