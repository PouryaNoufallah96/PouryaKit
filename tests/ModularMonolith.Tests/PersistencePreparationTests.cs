using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Errors;
using Catalog.Products.Features.CreateProduct;
using Catalog.Products.Features.GetProduct;
using Catalog.Products.Features.ListProducts;
using Catalog.Products.Features.RemoveProducts;
using Catalog.Products.Features.RenameProduct;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Host;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

public sealed class PersistencePreparationTests(AppFixture app) : HostTest<AppFixture>(app)
{
    [Fact]
    public async Task Audit_fields_carry_the_actor_and_the_clock_of_the_operation_that_created_and_last_changed_it()
    {
        var monday = new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);
        using var clock = new TestClock(monday);
        // Operations read the Host's TimeProvider, so this Host is built with the test clock in it.
        var builder = ModularMonolithHost.CreateBuilder(
            ["--environment", "Development", $"--ConnectionStrings:app={Fixture.ConnectionString}"]
        );
        builder.Services.AddSingleton<TimeProvider>(clock.Time);
        using var host = builder.Build();
        var catalog = host.Services.GetRequiredService<IGatewayProcessor<CatalogModule>>();
        var category = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();

        var alice = new OperationContext(Actor.Person("alice"));
        await catalog.SendAsync(new CreateCategoryCommand(category, "Lamps"), alice, TestContext.Current.CancellationToken);
        await catalog.SendAsync(
            new CreateProductCommand(product, category, "Lamp"),
            alice.Continue(),
            TestContext.Current.CancellationToken
        );
        var created = await AuditAsync();
        clock.Time.Advance(TimeSpan.FromHours(1));
        await catalog.SendAsync(
            new RenameProductCommand(product, "Desk lamp"),
            new OperationContext(Actor.Person("bob")),
            TestContext.Current.CancellationToken
        );

        Assert.Equal([monday.UtcDateTime, "alice", monday.UtcDateTime, "alice"], created);
        Assert.Equal([monday.UtcDateTime, "alice", monday.AddHours(1).UtcDateTime, "bob"], await AuditAsync());
    }

    [Fact]
    public async Task A_removed_product_stays_marked_deleted_is_invisible_to_every_read_and_leaves_its_category_count()
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var category = Guid.CreateVersion7();
        var lamp = Guid.CreateVersion7();
        var desk = Guid.CreateVersion7();
        await catalog.SendAsync(new CreateCategoryCommand(category, "Lamps"), TestContext.Current.CancellationToken);
        await catalog.SendAsync(new CreateProductCommand(lamp, category, "Lamp"), TestContext.Current.CancellationToken);
        await catalog.SendAsync(new CreateProductCommand(desk, category, "Desk"), TestContext.Current.CancellationToken);

        var removed = await catalog.SendAsync(new RemoveProductsCommand(["Lamp"]), TestContext.Current.CancellationToken);

        Assert.Equal(1, removed.Value);
        Assert.Equal(
            [[true, GatewayTestExtensions.TestActor.Id, true, 2L]],
            await Fixture.RowsAsync(
                $"select is_deleted, deleted_by, deleted_at is not null, version from catalog.products where id = '{lamp}'"
            )
        );
        Assert.Equal(1, await Fixture.ScalarAsync<int>("select product_count from catalog.categories"));
        Assert.Equal(new ProductsFound(ById: false, ByName: 0), await FindInCatalogAsync(new FindProductQuery(lamp, "Lamp")));
        Assert.Equal(
            ProductErrors.NotFound,
            Assert.Single((await catalog.QueryAsync(new GetProductQuery(lamp), TestContext.Current.CancellationToken)).Errors)
        );
        Assert.Equal(
            [desk],
            (await catalog.QueryAsync(new ListProductsQuery(), TestContext.Current.CancellationToken)).Value.Select(
                product => product.Id.Value
            )
        );
    }

    [Fact]
    public async Task Removing_a_soft_deletable_aggregate_keeps_what_it_owns()
    {
        var stub = Fixture.Gateway<StubModule>();
        var folder = Guid.CreateVersion7();
        await stub.SendAsync(new CreateFolderCommand(folder), TestContext.Current.CancellationToken);

        await stub.SendAsync(new EditFolderCommand(folder, FolderEdit.Remove), TestContext.Current.CancellationToken);

        Assert.Equal([[true, "grey"]], await Fixture.RowsAsync("select is_deleted, label_colour from stub.folders"));
        Assert.Equal(2L, await Fixture.ScalarAsync<long>("select count(*) from stub.pages"));
    }

    [Fact]
    public async Task Removing_an_aggregate_that_is_not_soft_deletable_deletes_its_row()
    {
        var stub = Fixture.Gateway<StubModule>();
        await stub.SendAsync(new WriteNoteCommand("kept"), TestContext.Current.CancellationToken);
        await stub.SendAsync(new WriteNoteCommand("removed"), TestContext.Current.CancellationToken);

        await stub.SendAsync(new RemoveNotesCommand("removed"), TestContext.Current.CancellationToken);

        Assert.Equal([["kept"]], await Fixture.RowsAsync("select text from stub.notes"));
    }

    [Fact]
    public async Task The_version_advances_exactly_once_for_each_operation_that_changes_any_part_of_the_aggregate()
    {
        var stub = Fixture.Gateway<StubModule>();
        var folder = Guid.CreateVersion7();
        await stub.SendAsync(new CreateFolderCommand(folder), TestContext.Current.CancellationToken);
        var versions = new List<long> { await VersionAsync() };

        // Renamed by the handler, relabelled by a nested command, and given a page by a Domain Event Handler.
        await stub.SendAsync(new ReworkFolderCommand(folder), TestContext.Current.CancellationToken);
        versions.Add(await VersionAsync());
        var reworked = await Fixture.RowsAsync(
            "select name, label_colour, (select count(*) from stub.pages) from stub.folders"
        );
        foreach (var edit in new[] { FolderEdit.Relabel, FolderEdit.RewritePage, FolderEdit.TearOutPage, FolderEdit.Nothing })
        {
            await stub.SendAsync(new EditFolderCommand(folder, edit, Colour: "blue"), TestContext.Current.CancellationToken);
            versions.Add(await VersionAsync());
        }

        Assert.Equal([["reworked", "red", 3L]], reworked);
        // Created; reworked three ways; the owned label alone; one page alone; one page torn out; nothing.
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 5L], versions);
        Assert.Equal(
            [["added on rename"], ["rewritten"]],
            await Fixture.RowsAsync("select text from stub.pages order by text")
        );
    }

    private Task<long> VersionAsync() => Fixture.ScalarAsync<long>("select version from stub.folders");

    private async Task<object[]> AuditAsync() =>
        Assert.Single(
            await Fixture.RowsAsync("select created_at, created_by, last_changed_at, last_changed_by from catalog.products")
        );

    // The probe is test code, which Catalog's Gateway refuses; the test begins a Catalog Operation itself.
    private async Task<ProductsFound> FindInCatalogAsync(FindProductQuery query)
    {
        await using var scope = Fixture.Host.Services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(new OperationContext(GatewayTestExtensions.TestActor), typeof(CatalogModule));
        return (
            await scope.ServiceProvider.GetRequiredService<IDispatcher>().QueryAsync(query, TestContext.Current.CancellationToken)
        ).Value;
    }
}
