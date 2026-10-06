using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Errors;
using Catalog.Products.Features.CreateProduct;
using Catalog.Products.Features.ImportProducts;
using Catalog.Products.Features.RemoveProducts;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
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
        // A product is soft-deletable: removing one marks it deleted and keeps its row.
        Assert.Equal([["Desk"]], await Fixture.RowsAsync("select name from catalog.products where not is_deleted"));
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
            new ImportProductsCommand(category, [" Lamp", "Desk "]),
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
    public async Task An_import_that_gives_a_name_twice_fails_and_creates_nothing()
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var category = await CreateCategoryAsync(catalog);
        await catalog.SendAsync(
            new CreateProductCommand(Guid.CreateVersion7(), category, "Lamp"),
            TestContext.Current.CancellationToken
        );

        // Lamp is in use, so skipping names in use would hide that it is given twice.
        var imported = await catalog.SendAsync(
            new ImportProductsCommand(category, ["Lamp", "Lamp ", "Desk"]),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(ProductErrors.NameRepeated, Assert.Single(imported.Errors));
        Assert.Equal([["Lamp"]], await Fixture.RowsAsync("select name from catalog.products"));
        Assert.Equal(1, await Fixture.ScalarAsync<int>("select product_count from catalog.categories"));
    }

    [Fact]
    public async Task The_generic_and_the_product_repository_track_in_the_same_unit_of_work()
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var category = await CreateCategoryAsync(catalog);
        var id = Guid.CreateVersion7();

        // The probe is test code, so no Gateway runs it: Catalog's Gateway refuses an operation another Module
        // registered. The test begins a scope as a Catalog Operation itself, as a delivery step does.
        await using var scope = Fixture.Host.Services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(new OperationContext(GatewayTestExtensions.TestActor), typeof(CatalogModule));
        var probed = await scope
            .ServiceProvider.GetRequiredService<IDispatcher>()
            .SendAsync(new ProbeProductRepositoriesCommand(id, category), TestContext.Current.CancellationToken);

        Assert.True(probed.Value);
        Assert.Equal(id, await Fixture.ScalarAsync<Guid>("select id from catalog.products"));
    }

    [Fact]
    public async Task A_query_sent_from_a_command_cannot_add_an_aggregate_through_the_repository()
    {
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Fixture
                .Gateway<StubModule>()
                .SendAsync(new WriteNoteThroughQueryCommand("nested"), TestContext.Current.CancellationToken)
        );

        Assert.StartsWith("Code that only reads, such as a query, an authorizer, a validator or a step of the Unit of Work that only reads, asked to write", thrown.Message);
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.notes"));
    }

    private static async Task<Guid> CreateCategoryAsync(IGatewayProcessor<CatalogModule> catalog) =>
        (
            await catalog.SendAsync(
                new CreateCategoryCommand(Guid.CreateVersion7(), "Furniture"),
                TestContext.Current.CancellationToken
            )
        ).Value;
}
