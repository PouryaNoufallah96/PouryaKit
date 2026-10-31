using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Errors;
using Catalog.Products.Features.CreateProduct;
using Catalog.Products.Features.GetProduct;
using Catalog.Products.Features.ListProducts;
using Catalog.Products.Features.RemoveProducts;
using Catalog.Products.Features.RenameProduct;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Modularity;
using PouryaKit.Persistence;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

public sealed class PersistencePreparationTests(AppFixture app) : HostTest<AppFixture>(app)
{
    [Fact]
    public async Task Audit_fields_carry_the_actor_and_the_clock_of_the_operation_that_created_and_last_changed_it()
    {
        var monday = new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);
        using var clock = new TestClock(monday);
        // Operations read the Host's TimeProvider, so this Host is built with the test clock in it. It is started: a new
        // product's Notification is written to the Outbox, which needs Wolverine running.
        await using var host = AppFixture.ComposeHost(
            "Development",
            Fixture.ConnectionString,
            Fixture.Logs,
            builder => builder.Services.AddSingleton<TimeProvider>(clock.Time)
        );
        await HostFixture.StartHostAsync(host, TestContext.Current.CancellationToken);
        var catalog = host.Services.GetRequiredService<IGatewayProcessor<CatalogModule>>();
        var category = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();

        var alice = new OperationContext(Actor.Person("alice"), AppFixture.TestTenant);
        await catalog.SendAsync(
            new CreateCategoryCommand(category, "Lamps"),
            alice,
            TestContext.Current.CancellationToken
        );
        await catalog.SendAsync(
            new CreateProductCommand(product, category, "Lamp"),
            alice.Continue(),
            TestContext.Current.CancellationToken
        );
        var created = await AuditAsync();
        clock.Time.Advance(TimeSpan.FromHours(1));
        await catalog.SendAsync(
            new RenameProductCommand(product, "Desk lamp"),
            new OperationContext(Actor.Person("bob"), AppFixture.TestTenant),
            TestContext.Current.CancellationToken
        );
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal([monday.UtcDateTime, "alice", monday.UtcDateTime, "alice"], created);
        Assert.Equal(
            [monday.UtcDateTime, "alice", monday.AddHours(1).UtcDateTime, "bob"],
            await AuditAsync()
        );
    }

    [Fact]
    public async Task A_removed_product_stays_marked_deleted_is_invisible_to_every_read_and_leaves_its_category_count()
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var category = Guid.CreateVersion7();
        var lamp = Guid.CreateVersion7();
        var desk = Guid.CreateVersion7();
        await catalog.SendAsync(
            new CreateCategoryCommand(category, "Lamps"),
            TestContext.Current.CancellationToken
        );
        await catalog.SendAsync(
            new CreateProductCommand(lamp, category, "Lamp"),
            TestContext.Current.CancellationToken
        );
        await catalog.SendAsync(
            new CreateProductCommand(desk, category, "Desk"),
            TestContext.Current.CancellationToken
        );

        var removed = await catalog.SendAsync(
            new RemoveProductsCommand(["Lamp"]),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(1, removed.Value);
        Assert.Equal(
            [
                [true, GatewayTestExtensions.TestActor.Id, true, 2L],
            ],
            await Fixture.RowsAsync(
                $"select is_deleted, deleted_by, deleted_at is not null, version from catalog.products where id = '{lamp}'"
            )
        );
        Assert.Equal(
            1,
            await Fixture.ScalarAsync<int>("select product_count from catalog.categories")
        );
        Assert.Equal(
            new ProductsFound(ById: false, ByName: 0),
            await FindInCatalogAsync(new FindProductQuery(lamp, "Lamp"))
        );
        Assert.Equal(
            ProductErrors.NotFound,
            Assert.Single(
                (
                    await catalog.QueryAsync(
                        new GetProductQuery(lamp),
                        TestContext.Current.CancellationToken
                    )
                ).Errors
            )
        );
        Assert.Equal(
            [desk],
            (
                await catalog.QueryAsync(
                    new ListProductsQuery(new PageRequest()),
                    TestContext.Current.CancellationToken
                )
            ).Value.Items.Select(product => product.Id.Value)
        );
    }

    [Fact]
    public async Task Removing_a_soft_deletable_aggregate_keeps_what_it_owns()
    {
        var stub = Fixture.Gateway<StubModule>();
        var folder = Guid.CreateVersion7();
        await stub.SendAsync(
            new CreateFolderCommand(folder),
            TestContext.Current.CancellationToken
        );

        await stub.SendAsync(
            new EditFolderCommand(folder, FolderEdit.Remove),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            [
                [true, "grey"],
            ],
            await Fixture.RowsAsync("select is_deleted, label_colour from stub.folders")
        );
        Assert.Equal(2L, await Fixture.ScalarAsync<long>("select count(*) from stub.pages"));
    }

    [Fact]
    public async Task Removing_an_aggregate_that_is_not_soft_deletable_deletes_its_row()
    {
        var stub = Fixture.Gateway<StubModule>();
        await stub.SendAsync(new WriteNoteCommand("kept"), TestContext.Current.CancellationToken);
        await stub.SendAsync(
            new WriteNoteCommand("removed"),
            TestContext.Current.CancellationToken
        );

        await stub.SendAsync(
            new RemoveNotesCommand("removed"),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            [
                ["kept"],
            ],
            await Fixture.RowsAsync("select text from stub.notes")
        );
    }

    [Fact]
    public async Task The_version_advances_exactly_once_for_each_operation_that_changes_any_part_of_the_aggregate()
    {
        var stub = Fixture.Gateway<StubModule>();
        var folder = Guid.CreateVersion7();
        await stub.SendAsync(
            new CreateFolderCommand(folder),
            TestContext.Current.CancellationToken
        );
        var versions = new List<long> { await VersionAsync() };

        // Renamed by the handler, relabelled by a nested command, and given a page by a Domain Event Handler.
        await stub.SendAsync(
            new ReworkFolderCommand(folder),
            TestContext.Current.CancellationToken
        );
        versions.Add(await VersionAsync());
        var reworked = await Fixture.RowsAsync(
            "select name, label_colour, (select count(*) from stub.pages) from stub.folders"
        );
        foreach (
            var edit in new[]
            {
                FolderEdit.Relabel,
                FolderEdit.RewritePage,
                FolderEdit.TearOutPage,
                FolderEdit.PeelSticker,
                FolderEdit.Nothing,
            }
        )
        {
            await stub.SendAsync(
                new EditFolderCommand(folder, edit, Colour: "blue"),
                TestContext.Current.CancellationToken
            );
            versions.Add(await VersionAsync());
        }

        Assert.Equal(
            [
                ["reworked", "red", 3L],
            ],
            reworked
        );
        // Created; reworked three ways; the owned label alone; one page alone; one page torn out; the sticker peeled
        // off, its optional key set to null; nothing.
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L, 6L], versions);
        Assert.Equal(
            [
                ["added on rename"],
                ["rewritten"],
            ],
            await Fixture.RowsAsync("select text from stub.pages order by text")
        );
        Assert.Equal(
            [
                ["fragile", DBNull.Value],
            ],
            await Fixture.RowsAsync("select text, folder_id from stub.stickers")
        );
    }

    [Fact]
    public async Task A_page_moved_to_another_folder_advances_and_stamps_both_folders_once()
    {
        var stub = Fixture.Gateway<StubModule>();
        Guid from = Guid.CreateVersion7(),
            to = Guid.CreateVersion7();
        await stub.SendAsync(new CreateFolderCommand(from), TestContext.Current.CancellationToken);
        await stub.SendAsync(new CreateFolderCommand(to), TestContext.Current.CancellationToken);

        var moved = await stub.SendAsync(
            new MovePageCommand(from, to, "first page"),
            new OperationContext(Actor.Person("carol")),
            TestContext.Current.CancellationToken
        );

        Assert.True(moved.IsSuccess);
        Assert.Equal(
            [
                [2L, "carol", 1L],
                [2L, "carol", 3L],
            ],
            await Fixture.RowsAsync(
                $"""
                select version, last_changed_by, (select count(*) from stub.pages where folder_id = folders.id)
                from stub.folders order by id = '{to}'
                """
            )
        );
    }

    [Fact(Timeout = 60_000)]
    public async Task Two_operations_that_move_the_same_page_out_of_one_folder_commit_once_and_the_other_fails_with_a_conflict()
    {
        var stub = Fixture.Gateway<StubModule>();
        Guid from = Guid.CreateVersion7(),
            first = Guid.CreateVersion7(),
            second = Guid.CreateVersion7();
        foreach (var folder in new[] { from, first, second })
        {
            await stub.SendAsync(
                new CreateFolderCommand(folder),
                TestContext.Current.CancellationToken
            );
        }

        Hold firstHold = new(),
            secondHold = new();
        var firstRun = SendHeldAsync(new MovePageCommand(from, first, "first page"), firstHold);
        var secondRun = SendHeldAsync(new MovePageCommand(from, second, "first page"), secondHold);
        await Task.WhenAll(firstHold.Changed.Task, secondHold.Changed.Task)
            .WaitAsync(TestContext.Current.CancellationToken);
        firstHold.Released.SetResult();
        var firstResult = await firstRun;
        secondHold.Released.SetResult();
        var secondResult = await secondRun;

        Assert.True(firstResult.IsSuccess);
        Assert.Equal(UnitOfWorkErrors.ConcurrencyConflict, Assert.Single(secondResult.Errors));
        // Each folder was created with two pages; only the commit that won moved one.
        Guid[] folders = [from, first, second];
        Assert.Equal(
            [
                [from, 2L, 1L],
                [first, 2L, 3L],
                [second, 1L, 2L],
            ],
            (
                await Fixture.RowsAsync(
                    "select id, version, (select count(*) from stub.pages where folder_id = folders.id) from stub.folders"
                )
            ).OrderBy(row => Array.IndexOf(folders, (Guid)row[0]))
        );
    }

    [Fact]
    public async Task Changing_a_part_of_an_aggregate_the_operation_did_not_load_throws_and_writes_nothing()
    {
        var stub = Fixture.Gateway<StubModule>();
        await stub.SendAsync(
            new CreateFolderCommand(Guid.CreateVersion7()),
            TestContext.Current.CancellationToken
        );

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            stub.SendAsync(
                new RewritePageUnloadedCommand("first page"),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains("which this Operation did not load", thrown.Message);
        Assert.Equal(1L, await VersionAsync());
        Assert.Equal(
            0L,
            await Fixture.ScalarAsync<long>(
                "select count(*) from stub.pages where text = 'rewritten unloaded'"
            )
        );
    }

    [Fact]
    public async Task Removing_a_product_twice_in_one_operation_lowers_its_categorys_count_once()
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var category = Guid.CreateVersion7();
        await catalog.SendAsync(
            new CreateCategoryCommand(category, "Lamps"),
            TestContext.Current.CancellationToken
        );
        await catalog.SendAsync(
            new CreateProductCommand(Guid.CreateVersion7(), category, "Lamp"),
            TestContext.Current.CancellationToken
        );

        await using var scope = Fixture.Host.Services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(
            new OperationContext(GatewayTestExtensions.TestActor, AppFixture.TestTenant),
            typeof(CatalogModule)
        );
        var removed = await scope
            .ServiceProvider.GetRequiredService<IDispatcher>()
            .SendAsync(
                new RemoveProductsTwiceCommand("Lamp"),
                TestContext.Current.CancellationToken
            );

        Assert.True(removed.IsSuccess);
        Assert.Equal(
            0,
            await Fixture.ScalarAsync<int>("select product_count from catalog.categories")
        );
        Assert.True(await Fixture.ScalarAsync<bool>("select is_deleted from catalog.products"));
    }

    // Runs the command in a Stub Operation of its own that waits, after its change, until the test lets it go.
    private async Task<Result> SendHeldAsync(Command command, Hold hold)
    {
        await Task.Yield();
        await using var scope = Fixture.Host.Services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(
            new OperationContext(GatewayTestExtensions.TestActor),
            typeof(StubModule)
        );
        return await scope
            .ServiceProvider.GetRequiredService<IDispatcher>()
            .SendAsync(
                new HoldChangeCommand(
                    async (dispatcher, ct) => await dispatcher.SendAsync(command, ct),
                    hold
                ),
                TestContext.Current.CancellationToken
            );
    }

    [Fact]
    public async Task Removing_an_aggregate_resolves_its_cascades_before_the_save_so_the_save_changes_nothing_further()
    {
        var stub = Fixture.Gateway<StubModule>();
        var tray = Guid.CreateVersion7();
        await stub.SendAsync(
            new CreateTrayCommand(tray),
            new OperationContext(Actor.Person("alice")),
            TestContext.Current.CancellationToken
        );

        var removed = await stub.SendAsync(
            new EditTrayCommand(tray, TrayEdit.Remove),
            new OperationContext(Actor.Person("bob")),
            TestContext.Current.CancellationToken
        );

        // The card, on an optional key, is kept with its key cleared, and stamped as changed by the removal.
        Assert.Equal(
            [
                [DBNull.Value, "alice", "bob"],
            ],
            await Fixture.RowsAsync("select tray_id, created_by, last_changed_by from stub.cards")
        );
        // The pin, on a required key, cannot outlive the tray: it is deleted with it, though it is soft-deletable.
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.pins"));
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.trays"));
        Assert.Empty(removed.Value.Entries);
    }

    [Fact]
    public async Task A_soft_deletable_child_removed_from_its_aggregate_is_kept_marked_deleted_before_the_save()
    {
        var stub = Fixture.Gateway<StubModule>();
        var tray = Guid.CreateVersion7();
        await stub.SendAsync(new CreateTrayCommand(tray), TestContext.Current.CancellationToken);

        var pulled = await stub.SendAsync(
            new EditTrayCommand(tray, TrayEdit.PullPin),
            TestContext.Current.CancellationToken
        );

        // An orphan is deleted as soon as changes are detected, so the prepare step sees it and keeps it.
        Assert.Equal(
            [
                [tray, true],
            ],
            await Fixture.RowsAsync("select tray_id, is_deleted from stub.pins")
        );
        Assert.Equal(2L, await Fixture.ScalarAsync<long>("select version from stub.trays"));
        Assert.Empty(pulled.Value.Entries);
    }

    // The pin is first kept as a soft deletion, then its tray is deleted outright, and the database's cascade would
    // remove the row the save meant to update.
    [Fact]
    public async Task A_soft_deletable_child_taken_out_of_an_aggregate_that_is_then_deleted_is_deleted_with_it_before_the_save()
    {
        var stub = Fixture.Gateway<StubModule>();
        var tray = Guid.CreateVersion7();
        await stub.SendAsync(new CreateTrayCommand(tray), TestContext.Current.CancellationToken);

        var removed = await stub.SendAsync(
            new EditTrayCommand(tray, TrayEdit.PullPinThenRemove),
            TestContext.Current.CancellationToken
        );

        Assert.True(removed.IsSuccess);
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.pins"));
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.trays"));
        Assert.Empty(removed.Value.Entries);
    }

    [Fact]
    public async Task Children_of_two_aggregates_derived_from_one_base_change_in_one_operation()
    {
        var stub = Fixture.Gateway<StubModule>();
        Guid pencilCase = Guid.CreateVersion7(),
            bookCase = Guid.CreateVersion7();
        await stub.SendAsync(
            new CreateCasesCommand(pencilCase, bookCase),
            TestContext.Current.CancellationToken
        );

        var touched = await stub.SendAsync(
            new TouchCasesCommand(pencilCase, bookCase),
            TestContext.Current.CancellationToken
        );

        Assert.True(touched.IsSuccess);
        Assert.Equal(
            [
                [2L],
                [2L],
            ],
            await Fixture.RowsAsync("select version from stub.cases")
        );
        Assert.Equal("sharp", await Fixture.ScalarAsync<string>("select colour from stub.pencils"));
        Assert.Equal("final", await Fixture.ScalarAsync<string>("select title from stub.books"));
    }

    private Task<long> VersionAsync() =>
        Fixture.ScalarAsync<long>("select version from stub.folders");

    private async Task<object[]> AuditAsync() =>
        Assert.Single(
            await Fixture.RowsAsync(
                "select created_at, created_by, last_changed_at, last_changed_by from catalog.products"
            )
        );

    // The probe is test code, which Catalog's Gateway refuses; the test begins a Catalog Operation itself.
    private async Task<ProductsFound> FindInCatalogAsync(FindProductQuery query)
    {
        await using var scope = Fixture.Host.Services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(
            new OperationContext(GatewayTestExtensions.TestActor, AppFixture.TestTenant),
            typeof(CatalogModule)
        );
        return (
            await scope
                .ServiceProvider.GetRequiredService<IDispatcher>()
                .QueryAsync(query, TestContext.Current.CancellationToken)
        ).Value;
    }
}
