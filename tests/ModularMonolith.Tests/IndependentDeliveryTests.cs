using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Features.CreateProduct;
using PouryaKit.Domain;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

/// <summary>
/// Two Modules subscribe to one Integration Event, each under its own subscription: what one does, fails or leaves
/// behind never runs the other again.
/// </summary>
public sealed class IndependentDeliveryTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private const string WireName = "catalog.product-created.v1";

    [Fact]
    public async Task A_product_created_once_is_published_once_and_inventory_and_billing_each_apply_it_once()
    {
        var product = Guid.CreateVersion7();

        var (_, deliveries) = await Fixture.WaitForDeliveriesAsync(
            () => CreateProductAsync(product),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            [new Delivery("billing.product-created", WireName, false), new Delivery("inventory.product-created", WireName, false)],
            deliveries.Where(delivery => delivery.WireName == WireName).OrderBy(delivery => delivery.Subscription, StringComparer.Ordinal)
        );
        // Catalog published it once: one Receipt of the publishing Notification Handler, one delivery per subscription.
        var published = Assert.Single(
            Assert.Single(
                await Fixture.RowsAsync(
                    "select message_id from catalog.pouryakit_receipts where subscription = 'catalog.product-created.publish-product-created'"
                )
            )
        );
        // Each Module applied it in its own data, with a Receipt of its own keyed by the same identity.
        Assert.Equal([[product]], await Fixture.RowsAsync("select product_id from inventory.stock_items"));
        Assert.Equal([[product]], await Fixture.RowsAsync("select product_id from billing.price_lists"));
        Assert.Equal(
            [["billing.product-created", published], ["inventory.product-created", published]],
            await Fixture.RowsAsync(
                "select subscription, message_id from billing.pouryakit_receipts union all select subscription, message_id from inventory.pouryakit_receipts order by 1"
            )
        );
    }

    private async Task<Result<Guid>> CreateProductAsync(Guid product)
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var category = Guid.CreateVersion7();
        await catalog.SendAsync(new CreateCategoryCommand(category, $"Lamps {category}"), TestContext.Current.CancellationToken);
        return await catalog.SendAsync(
            new CreateProductCommand(product, category, $"Lamp {product}"),
            TestContext.Current.CancellationToken
        );
    }
}
