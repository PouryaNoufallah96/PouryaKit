using System.Diagnostics;
using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Features.CreateProduct;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using PouryaKit.Observability;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

// The trace of an Operation continues across RabbitMQ into the deliveries it causes in another Host (ADR 0009, #52
// amendment): ModularMonolith.Host with a broker (Catalog, Billing and Ledger) and Inventory.Host, each observing its
// Operations, joined only by the broker. An ActivityListener sees its own process only, so both Hosts run in this one.
[Collection<BrokerCollection>]
public sealed class CrossProcessTracingTests(BrokerFixture broker)
{
    private const string Publishing = "catalog.product-created.publish-product-created";
    private const string StockItems = "inventory.product-created";
    private const string TraceState = "kit=52,other=v1";
    private const string SubscriptionTag = "pouryakit.subscription";

    // CreateProduct commits a Notification; its delivery Operation in the Catalog Host publishes ProductCreatedV1; the
    // Inventory Host receives it from the broker and fans it out to Inventory's subscription, whose first attempt fails
    // and runs again from the store. With Wolverine's own source listened or not.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_product_created_in_the_catalog_host_and_stocked_by_inventory_host_is_one_trace_with_the_upstream_tracestate(
        bool wolverineListened
    )
    {
        var environment = await broker.CreateEnvironmentAsync();
        using var inventory = await environment.StartInventoryHostAsync();
        await using var catalog = await environment.StartCatalogHostAsync();
        using var telemetry = new RecordedTelemetry(wolverineListened);
        var product = Guid.CreateVersion7();

        Activity upstream;
        // The first attempt runs from the copy the fan-out hands over in memory, the second from the copy it stored.
        using (DeliveryProbes.FailOnceAfterReceipt(StockItems))
        {
            upstream = await CreateProductAsync(catalog, product);
            await BrokerEnvironment.WaitUntilAsync(
                environment.Inventory,
                $"exists (select from inventory.stock_items where product_id = '{product}')",
                "Inventory.Host never stocked the product."
            );
        }

        await BrokerEnvironment.WaitUntilSettledAsync(environment.Inventory);

        var trace = telemetry.Of(upstream.TraceId);
        var publishing = Assert.Single(
            DeliveryProbes.ActivitiesOf(Publishing).OfType<Activity>(),
            activity => activity.TraceId == upstream.TraceId
        );
        var attempts = DeliveryProbes
            .ActivitiesOf(StockItems)
            .OfType<Activity>()
            .Where(activity => activity.TraceId == upstream.TraceId)
            .ToList();
        Assert.Equal(
            [OperationTelemetry.Threw, OperationTelemetry.Succeeded],
            attempts.Select(attempt => Tag(attempt, OperationTelemetry.OutcomeTag))
        );
        Assert.All(
            attempts,
            attempt =>
                Assert.Equal(
                    ("InventoryModule", StockItems, publishing.SpanId, TraceState),
                    (
                        Tag(attempt, OperationTelemetry.ModuleTag),
                        Tag(attempt, SubscriptionTag),
                        PublisherOf(attempt, trace, wolverineListened),
                        attempt.TraceStateString
                    )
                )
        );
        // Each delivery's Operation names its subscription, beside the Deliver command every one of them runs.
        Assert.Equal(Publishing, Tag(publishing, SubscriptionTag));
    }

    // The category is created first, outside the trace; CreateProduct runs inside an upstream activity standing in for a
    // request whose W3C context ASP.NET Core restored, with a tracestate.
    private static async Task<Activity> CreateProductAsync(IHost catalog, Guid product)
    {
        var gateway = catalog.Services.GetRequiredService<IGatewayProcessor<CatalogModule>>();
        var category = Guid.CreateVersion7();
        Assert.True(
            (
                await gateway.SendAsync(
                    new CreateCategoryCommand(category, $"Lamps {category}"),
                    TestContext.Current.CancellationToken
                )
            ).IsSuccess
        );
        using var upstream = RecordedTelemetry.Upstream.StartActivity(
            "request",
            ActivityKind.Server
        )!;
        upstream.TraceStateString = TraceState;
        Assert.True(
            (
                await gateway.SendAsync(
                    new CreateProductCommand(product, category, $"Lamp {product}"),
                    new OperationContext(Actor.Person("ada"), AppFixture.TestTenant),
                    TestContext.Current.CancellationToken
                )
            ).IsSuccess
        );
        return upstream;
    }

    // The span a delivery's copy named as its parent. When Wolverine's source is listened, the delivery's parent is
    // Wolverine's span of the copy, whose parent is its span of the envelope the broker delivered, whose parent is the
    // publishing Operation; otherwise the publishing Operation itself.
    private static ActivitySpanId PublisherOf(
        Activity delivery,
        IReadOnlyList<Activity> trace,
        bool wolverineListened
    )
    {
        if (!wolverineListened)
        {
            return delivery.ParentSpanId;
        }

        var copy = Assert.Single(trace, activity => activity.SpanId == delivery.ParentSpanId);
        var received = Assert.Single(trace, activity => activity.SpanId == copy.ParentSpanId);
        Assert.Equal(["Wolverine", "Wolverine"], [copy.Source.Name, received.Source.Name]);
        return received.ParentSpanId;
    }

    private static string? Tag(Activity activity, string name) =>
        activity.GetTagItem(name) as string;
}
