using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Errors;
using Catalog.Products.Features.CreateProduct;
using Catalog.Products.Features.ImportProducts;
using Catalog.Products.Features.RemoveProducts;
using ModularMonolith.Tests.Stub;
using PouryaKit.Modularity;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

public sealed class RepositoryTests(AppFixture app) : HostTest<AppFixture>(app)
{
    [Fact]
    public async Task A_command_removes_the_products_a_named_specification_selects()
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var category = await CreateCategoryAsync(catalog);
        foreach (var name in new[] { "Lamp", "Desk", "Chair" })
        {
            await catalog.SendAsync(
                new CreateProductCommand(Guid.CreateVersion7(), category, name),
                TestContext.Current.CancellationToken
            );
        }

        var removed = await catalog.SendAsync(
            new RemoveProductsCommand(["Lamp", "Chair", "Sofa"]),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(2, removed.Value);
        Assert.Equal([["Desk"]], await Fixture.RowsAsync("select name from catalog.products"));
    }

    [Fact]
    public async Task An_import_creates_a_product_only_for_each_name_no_product_has()
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var category = await CreateCategoryAsync(catalog);
        await catalog.SendAsync(
            new CreateProductCommand(Guid.CreateVersion7(), category, "Lamp"),
            TestContext.Current.CancellationToken
        );

        var imported = await catalog.SendAsync(
            new ImportProductsCommand(category, [" Lamp", "Desk", "Desk "]),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(1, imported.Value);
        Assert.Equal(
            [["Desk"], ["Lamp"]],
            await Fixture.RowsAsync("select name from catalog.products order by name")
        );
        Assert.Equal(2, await Fixture.ScalarAsync<int>("select product_count from catalog.categories"));
    }

    [Fact]
    public async Task An_import_that_fails_after_adding_through_the_product_repository_leaves_nothing_written()
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var category = await CreateCategoryAsync(catalog);

        var result = await catalog.SendAsync(
            new ImportProductsCommand(category, ["Desk", " "]),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(ProductErrors.NameRequired, Assert.Single(result.Errors));
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from catalog.products"));
    }

    [Fact]
    public async Task The_generic_and_the_product_repository_track_in_the_same_unit_of_work()
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var category = await CreateCategoryAsync(catalog);
        var id = Guid.CreateVersion7();

        var probed = await catalog.SendAsync(
            new ProbeProductRepositoriesCommand(id, category),
            TestContext.Current.CancellationToken
        );

        Assert.True(probed.Value);
        Assert.Equal(id, await Fixture.ScalarAsync<Guid>("select id from catalog.products"));
    }

    private static async Task<Guid> CreateCategoryAsync(IGatewayProcessor<CatalogModule> catalog) =>
        (
            await catalog.SendAsync(
                new CreateCategoryCommand(Guid.CreateVersion7(), "Furniture"),
                TestContext.Current.CancellationToken
            )
        ).Value;
}
