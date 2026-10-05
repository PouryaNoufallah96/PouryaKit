using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Features.CreateProduct;
using Catalog.Products.Features.RemoveProducts;
using Catalog.Products.Features.RenameProduct;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Modularity;
using PouryaKit.Persistence.Postgres;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

public sealed class SnapshotTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private const string InventorySubscription = "inventory.catalog-products";
    private const string FolderSubscription = "stub.folder-snapshots";

    private IntegrationEventLog Received => Fixture.Host.Services.GetRequiredService<IntegrationEventLog>();

    [Fact]
    public async Task Inventorys_copy_follows_each_change_of_a_product_with_the_version_catalog_committed()
    {
        var product = Guid.CreateVersion7();
        List<object[]> copies = [];

        await ChangeProductAsync(() => CreateProductAsync(product, "Lamp"));
        copies.AddRange(await CopyAndProductAsync(product));
        await ChangeProductAsync(() => RenameProductAsync(product, "Desk lamp"));
        copies.AddRange(await CopyAndProductAsync(product));

        Assert.Equal([["Lamp", 1L, 1L], ["Desk lamp", 2L, 2L]], copies);
    }

    [Fact]
    public async Task One_operation_that_changes_a_folder_three_ways_stores_one_snapshot_with_the_persisted_version()
    {
        var folder = Guid.CreateVersion7();
        await Fixture.WaitForDeliveriesAsync(() => StubAsync(new CreateFolderCommand(folder)), TestContext.Current.CancellationToken);

        // Renamed by the handler, relabelled by a nested command, and given a page by a Domain Event Handler.
        var (_, deliveries) = await Fixture.WaitForDeliveriesAsync(
            () => StubAsync(new ReworkFolderCommand(folder)),
            TestContext.Current.CancellationToken
        );

        Assert.Single(deliveries, delivery => delivery.Subscription == FolderSubscription);
        Assert.Equal(new FolderSnapshotV1(folder, "reworked", "red", 3, false, 2L), FolderSnapshots(folder)[^1]);
        Assert.Equal(2L, await Fixture.ScalarAsync<long>($"select version from stub.folders where id = '{folder}'"));
    }

    [Fact]
    public async Task One_operation_that_changes_two_folders_stores_one_snapshot_of_each()
    {
        Guid from = Guid.CreateVersion7(), to = Guid.CreateVersion7();
        await Fixture.WaitForDeliveriesAsync(() => StubAsync(new CreateFolderCommand(from)), TestContext.Current.CancellationToken);
        await Fixture.WaitForDeliveriesAsync(() => StubAsync(new CreateFolderCommand(to)), TestContext.Current.CancellationToken);

        var (_, deliveries) = await Fixture.WaitForDeliveriesAsync(
            () => StubAsync(new MovePageCommand(from, to, "first page")),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(2, deliveries.Count(delivery => delivery.Subscription == FolderSubscription));
        Assert.Equal(
            [new FolderSnapshotV1(from, "drafts", "grey", 1, false, 2L), new FolderSnapshotV1(to, "drafts", "grey", 3, false, 2L)],
            [FolderSnapshots(from)[^1], FolderSnapshots(to)[^1]]
        );
    }

    [Theory]
    [InlineData(nameof(FolderEdit.Nothing))]
    [InlineData(nameof(FolderEdit.RelabelThenRefuse))]
    public async Task An_operation_that_changes_no_aggregate_or_rolls_back_stores_no_snapshot(string edit)
    {
        var folder = Guid.CreateVersion7();
        await Fixture.WaitForDeliveriesAsync(() => StubAsync(new CreateFolderCommand(folder)), TestContext.Current.CancellationToken);

        await Fixture.AssertNothingSentAsync(
            () => StubAsync(new EditFolderCommand(folder, Enum.Parse<FolderEdit>(edit))),
            TestContext.Current.CancellationToken
        );

        Assert.Equal([["grey", 1L]], await Fixture.RowsAsync("select label_colour, version from stub.folders"));
    }

    [Fact]
    public async Task A_change_made_only_through_a_child_of_an_aggregate_publishes_its_snapshot()
    {
        var folder = Guid.CreateVersion7();
        await Fixture.WaitForDeliveriesAsync(() => StubAsync(new CreateFolderCommand(folder)), TestContext.Current.CancellationToken);

        var (_, deliveries) = await Fixture.WaitForDeliveriesAsync(
            () => StubAsync(new EditFolderCommand(folder, FolderEdit.RewritePage)),
            TestContext.Current.CancellationToken
        );

        Assert.Single(deliveries, delivery => delivery.Subscription == FolderSubscription);
        Assert.Equal(new FolderSnapshotV1(folder, "drafts", "grey", 2, false, 2L), FolderSnapshots(folder)[^1]);
    }

    [Fact]
    public async Task Removing_a_product_publishes_its_tombstone_with_a_newer_version_and_inventorys_copy_is_gone()
    {
        var product = Guid.CreateVersion7();
        await ChangeProductAsync(() => CreateProductAsync(product, "Lamp"));
        await ChangeProductAsync(() => RenameProductAsync(product, "Desk lamp"));

        await ChangeProductAsync(async () =>
            (Result)await Fixture.Gateway<CatalogModule>().SendAsync(new RemoveProductsCommand(["Desk lamp"]), TestContext.Current.CancellationToken)
        );

        Assert.Empty(await Fixture.RowsAsync("select * from inventory.catalog_products"));
        // The last version Inventory applied is the Tombstone's, one newer than the renamed product's: what Catalog keeps.
        Assert.Equal(
            [[3L, 3L, true]],
            await Fixture.RowsAsync(
                $"select applied.version, products.version, products.is_deleted from inventory.pouryakit_snapshot_versions applied join catalog.products on products.id::text = applied.aggregate_id where applied.subscription = '{InventorySubscription}'"
            )
        );
    }

    [Fact]
    public async Task Removing_an_aggregate_deleted_outright_publishes_its_tombstone_with_a_newer_version()
    {
        var tray = Guid.CreateVersion7();
        await Fixture.WaitForDeliveriesAsync(() => StubAsync(new CreateTrayCommand(tray)), TestContext.Current.CancellationToken);

        await Fixture.WaitForDeliveriesAsync(
            async () => (Result)await Fixture.Gateway<StubModule>().SendAsync(new EditTrayCommand(tray, TrayEdit.Remove), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.trays"));
        Assert.Equal(
            [new TraySnapshotV1(tray, 1, false, 1L), new TraySnapshotV1(tray, 1, true, 2L)],
            Received.All.Select(received => received.IntegrationEvent).OfType<TraySnapshotV1>().Where(snapshot => snapshot.TrayId == tray)
        );
    }

    [Fact]
    public async Task Snapshots_delivered_in_reverse_order_leave_the_newest()
    {
        var product = Guid.CreateVersion7();
        var envelopes = await ThreeVersionsAsync(product, remove: false);
        await ForgetCopiesAsync();

        foreach (var envelope in envelopes.Reverse())
        {
            await RedeliverAsync(envelope);
            await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal([[product, "Floor lamp", 3L]], await Fixture.RowsAsync("select product_id, name, version from inventory.catalog_products"));
    }

    [Theory(Timeout = 60_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Two_versions_delivered_concurrently_leave_the_newer(bool newerFirst)
    {
        var product = Guid.CreateVersion7();
        var envelopes = await ThreeVersionsAsync(product, remove: false);
        await ForgetCopiesAsync();
        Guid first = newerFirst ? envelopes[2] : envelopes[1], second = newerFirst ? envelopes[1] : envelopes[2];

        // The first holds its newer-than-anything version uncommitted; the second arrives and must decide against it.
        using (var hold = DeliveryProbes.HoldAfterVersion(InventorySubscription))
        {
            await RedeliverAsync(first);
            await hold.Held;
            await RedeliverAsync(second);
            await WaitForVersionWaitingAsync();
        }

        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);
        Assert.Equal([[product, "Floor lamp", 3L]], await Fixture.RowsAsync("select product_id, name, version from inventory.catalog_products"));
    }

    [Fact]
    public async Task A_live_snapshot_delivered_after_the_tombstone_does_not_bring_the_copy_back()
    {
        var product = Guid.CreateVersion7();
        var envelopes = await ThreeVersionsAsync(product, remove: true);
        await ForgetCopiesAsync();

        foreach (var envelope in new[] { envelopes[2], envelopes[1] })
        {
            await RedeliverAsync(envelope);
            await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);
        }

        Assert.Empty(await Fixture.RowsAsync("select * from inventory.catalog_products"));
    }

    [Fact]
    public async Task A_snapshot_delivered_again_changes_nothing()
    {
        var folder = Guid.CreateVersion7();
        await Fixture.WaitForDeliveriesAsync(() => StubAsync(new CreateFolderCommand(folder)), TestContext.Current.CancellationToken);

        Assert.Single(
            await Fixture.RowsAsync(
                $"update wolverine.wolverine_incoming_envelopes set status = 'Incoming', owner_id = 0 where received_at = 'local://{FolderSubscription}/' returning id"
            )
        );
        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);

        Assert.Single(FolderSnapshots(folder));
        // The redelivery carried the same identity, so its Receipt was the one already recorded.
        Assert.Equal(
            1L,
            await Fixture.ScalarAsync<long>($"select count(*) from stub.pouryakit_receipts where subscription = '{FolderSubscription}'")
        );
    }

    [Fact]
    public async Task A_snapshot_is_stored_in_the_commit_of_the_change_under_its_wire_name()
    {
        var product = Guid.CreateVersion7();
        List<object[]> productCommits = [];
        List<object[]> envelopes = [];

        using (
            CommitProbe.AfterCommit(async () =>
            {
                productCommits = await Fixture.RowsAsync($"select pg_xact_commit_timestamp(xmin) from catalog.products where id = '{product}'");
                envelopes = await Fixture.RowsAsync(
                    "select received_at, status, pg_xact_commit_timestamp(xmin) from wolverine.wolverine_incoming_envelopes where message_type = 'catalog.product-snapshot.v1'"
                );
                return true;
            })
        )
        {
            await ChangeProductAsync(() => CreateProductAsync(product, "Lamp"));
        }

        var committedAt = Assert.Single(Assert.Single(productCommits));
        Assert.IsType<DateTime>(committedAt);
        Assert.Equal([[$"local://{InventorySubscription}/", "Incoming", committedAt]], envelopes);
    }

    [Fact]
    public async Task A_snapshot_mapper_that_changes_its_aggregate_fails_the_operation_and_commits_nothing()
    {
        var folder = Guid.CreateVersion7();
        await Fixture.WaitForDeliveriesAsync(() => StubAsync(new CreateFolderCommand(folder)), TestContext.Current.CancellationToken);

        var thrown = await Fixture.AssertNothingSentAsync(
            () =>
                Assert.ThrowsAsync<InvalidOperationException>(() =>
                    StubAsync(new EditFolderCommand(folder, FolderEdit.Relabel, Colour: FolderSnapshotMapper.ChangesItsFolder))
                ),
            TestContext.Current.CancellationToken
        );

        Assert.StartsWith("A Snapshot mapper changed a Folder the Operation saves", thrown.Message);
        Assert.Equal([["drafts", "grey", 1L]], await Fixture.RowsAsync("select name, label_colour, version from stub.folders"));
    }

    [Theory]
    [InlineData(ShelfSnapshotMapper.AddsALabel, "A Snapshot mapper changed a Shelf the Operation saves")]
    [InlineData(ShelfSnapshotMapper.Resizes, "A Snapshot mapper changed a Shelf the Operation saves")]
    [InlineData(ShelfSnapshotMapper.Announces, "A Snapshot mapper raised a Domain Event on a Shelf")]
    public async Task A_snapshot_mapper_that_changes_a_value_in_place_or_a_complex_property_or_raises_a_domain_event_fails_the_operation(
        string mischief,
        string error
    )
    {
        // A new shelf, so it is already Added when its mapper runs: its state cannot show the change.
        var thrown = await Fixture.AssertNothingSentAsync(
            () => Assert.ThrowsAsync<InvalidOperationException>(() => StubAsync(new PutShelvesCommand([["A1", "B1", mischief]]))),
            TestContext.Current.CancellationToken
        );

        Assert.StartsWith(error, thrown.Message);
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.shelves"));
    }

    [Fact]
    public async Task Composite_keys_whose_parts_join_alike_keep_their_snapshots_apart()
    {
        await Fixture.WaitForDeliveriesAsync(
            () => StubAsync(new PutShelvesCommand([["a/b", "c"], ["a", "b/c"]])),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            [new ShelfSnapshotV1("a", "b/c", 0, false, 1L), new ShelfSnapshotV1("a/b", "c", 0, false, 1L)],
            Received.All.Select(received => received.IntegrationEvent).OfType<ShelfSnapshotV1>().Where(snapshot => snapshot.Aisle.StartsWith('a')).OrderBy(snapshot => snapshot.Aisle, StringComparer.Ordinal)
        );
        Assert.Equal(
            2L,
            await Fixture.ScalarAsync<long>("select count(distinct aggregate_id) from stub.pouryakit_snapshot_versions where subscription = 'stub.shelf-snapshots'")
        );
    }

    [Fact]
    public async Task A_snapshot_declared_for_a_shared_base_type_is_published_only_for_its_own_modules_aggregates()
    {
        Guid product = Guid.CreateVersion7(), leaflet = Guid.CreateVersion7();

        await ChangeProductAsync(() => CreateProductAsync(product, "Lamp"));
        await Fixture.WaitForDeliveriesAsync(() => StubAsync(new PrintLeafletCommand(leaflet, "Lamps")), TestContext.Current.CancellationToken);

        // Catalog's product is keyed by a product ID too, yet publishes only Catalog's own Snapshot.
        Assert.Equal(
            [new ProductKeyedSnapshotV1(nameof(Leaflet), leaflet, false, 1L)],
            Received.All.Select(received => received.IntegrationEvent).OfType<ProductKeyedSnapshotV1>().Where(snapshot => snapshot.ProductId == product || snapshot.ProductId == leaflet)
        );
        Assert.Equal([[product, 1L]], await Fixture.RowsAsync("select product_id, version from inventory.catalog_products"));
    }

    [Fact]
    public async Task A_snapshot_of_an_aggregate_whose_key_the_database_generates_fails_the_operation_that_creates_it()
    {
        var thrown = await Fixture.AssertNothingSentAsync(
            () => Assert.ThrowsAsync<InvalidOperationException>(() => StubAsync(new StartCounterCommand("visits"))),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            "A Snapshot of Counter is declared, but its key is generated by the database when it is saved, after the Snapshot is built. Give Counter a key the application sets, such as a Guid version 7.",
            thrown.Message
        );
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.counters"));
    }

    [Theory(Timeout = 60_000)]
    [InlineData(ShelfSnapshotMapper.SendsACommand, "A command was sent from a Snapshot mapper, a step that only reads.")]
    [InlineData(ShelfSnapshotMapper.AddsAShelf, "Code that only reads, such as a query, an authorizer, a validator or a step of the Unit of Work that only reads, asked to write")]
    [InlineData(ShelfSnapshotMapper.JoinsTheConnection, "Code that only reads, such as a query, an authorizer, a validator or a step of the Unit of Work that only reads, asked to write")]
    public async Task A_snapshot_mapper_that_sends_a_command_or_writes_is_refused_and_nothing_commits(string mischief, string error)
    {
        var thrown = await Fixture.AssertNothingSentAsync(
            () => Assert.ThrowsAsync<InvalidOperationException>(() => StubAsync(new PutShelvesCommand([["A1", "B1", mischief]]))),
            TestContext.Current.CancellationToken
        );

        Assert.StartsWith(error, thrown.Message);
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.shelves"));
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.note_tallies"));
    }

    // The source refusal leaves one way from a mapper to the Unit of Work's recheck after the Outbox write: a refused
    // command still marks the Operation rollback-only, so a mapper that swallows the refusal ends the Operation there.
    [Fact(Timeout = 60_000)]
    public async Task A_snapshot_mapper_that_swallows_the_refusal_of_its_command_fails_the_operation_after_the_outbox_is_written()
    {
        var result = await Fixture.AssertNothingSentAsync(
            () => StubAsync(new PutShelvesCommand([["A1", "B1", ShelfSnapshotMapper.SwallowsItsRefusedCommand]])),
            TestContext.Current.CancellationToken
        );

        Assert.Equal("Operation.CommandThrew", Assert.Single(result.Errors).Code);
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.shelves"));
    }

    [Fact]
    public async Task A_snapshot_declared_for_aggregates_of_two_mapped_hierarchies_fails_the_operation_that_would_publish_it()
    {
        var folder = Guid.CreateVersion7();

        var thrown = await WithDeclarationAsync(
            services => services.PublishSnapshot<IDeskItem, DeskItemSnapshotV1, DeskItemSnapshotMapper>("tests.desk-item.v1", typeof(StubModule)),
            stub => stub.SendAsync(new CreateFolderCommand(folder), TestContext.Current.CancellationToken)
        );

        Assert.Equal(
            $"The Snapshot {typeof(DeskItemSnapshotV1).FullName} is declared for {typeof(IDeskItem).FullName}, which covers aggregates of the separately mapped hierarchies of {typeof(Folder).FullName}, {typeof(Case).FullName}. Their keys are their own, so their Snapshots would share identities and versions, and a receiver would turn one aggregate's away for another's. Declare a Snapshot for each aggregate, or for a type of one mapped hierarchy.",
            thrown.Message
        );
        Assert.Equal(0L, await Fixture.ScalarAsync<long>($"select count(*) from stub.folders where id = '{folder}'"));
    }

    [Fact]
    public async Task A_snapshot_declared_for_a_base_class_of_one_mapped_hierarchy_is_published_for_each_of_its_aggregates()
    {
        Guid pencilCase = Guid.CreateVersion7(), bookCase = Guid.CreateVersion7();

        await Fixture.WaitForDeliveriesAsync(
            () => StubAsync(new CreateCasesCommand(pencilCase, bookCase)),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            [new CaseSnapshotV1(bookCase, nameof(BookCase), false, 1L), new CaseSnapshotV1(pencilCase, nameof(PencilCase), false, 1L)],
            Received.All.Select(received => received.IntegrationEvent).OfType<CaseSnapshotV1>()
                .Where(snapshot => snapshot.CaseId == pencilCase || snapshot.CaseId == bookCase)
                .OrderBy(snapshot => snapshot.Kind, StringComparer.Ordinal)
        );
    }

    [Fact]
    public async Task A_snapshot_mapper_that_writes_while_it_is_built_fails_the_operation_that_would_publish_it()
    {
        var leaflet = Guid.CreateVersion7();

        var thrown = await WithDeclarationAsync(
            services =>
                services.PublishSnapshot<Leaflet, LeafletSnapshotV1, LeafletSnapshotMapperThatWritesWhenBuilt>("tests.leaflet.v1", typeof(StubModule)),
            stub => stub.SendAsync(new PrintLeafletCommand(leaflet, "Lamps"), TestContext.Current.CancellationToken)
        );

        Assert.StartsWith("Code that only reads, such as a query, an authorizer, a validator or a step of the Unit of Work that only reads, asked to write", thrown.Message);
        Assert.Equal(0L, await Fixture.ScalarAsync<long>($"select count(*) from stub.leaflets where id = '{leaflet}'"));
    }

    [Fact]
    public async Task A_snapshot_declared_without_its_module_in_a_host_of_modules_fails_the_operation_that_would_publish_it()
    {
        var thrown = await WithDeclarationAsync(
            services => services.PublishSnapshot<Stub.Shelf, MislaidShelfSnapshotV1, MislaidShelfSnapshotMapper>("tests.mislaid-shelf.v1"),
            stub => stub.SendAsync(new PutShelvesCommand([["A1", "B1"]]), TestContext.Current.CancellationToken)
        );

        Assert.Equal(
            $"The Snapshot {typeof(MislaidShelfSnapshotV1).FullName} of {typeof(Stub.Shelf).FullName} is declared without a Module, but Shelf changed in an Operation of StubModule. Declare it with PublishSnapshot(wireName, module) in that Module's Composition Root.",
            thrown.Message
        );
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.shelves"));
    }

    [Fact]
    public async Task A_snapshot_declared_with_a_module_in_an_application_without_modules_fails_the_operation_that_would_publish_it()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production", $"--ConnectionStrings:app={Fixture.ConnectionString}"]);
        builder.UsePouryaKitWolverine(connectionName: "app");
        builder.Services.UsePouryaKitTestDelivery();
        builder.Services.UsePostgres<StubDbContext>(connectionName: "app", schema: "stub");
        builder.Services.AddPouryaKitCqrs(typeof(StubDbContext).Assembly);
        builder.Services.PublishSnapshot<Stub.Shelf, MislaidShelfSnapshotV1, MislaidShelfSnapshotMapper>("tests.mislaid-shelf.v1", typeof(StubModule));
        using var host = builder.Build();
        await HostFixture.StartHostAsync(host, TestContext.Current.CancellationToken);
        InvalidOperationException thrown;
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            scope.ServiceProvider.BeginOperation(new OperationContext(Actor.System("single-module")));
            thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                scope
                    .ServiceProvider.GetRequiredService<IDispatcher>()
                    .SendAsync(new PutShelvesCommand([["A1", "B1"]]), TestContext.Current.CancellationToken)
            );
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            $"The Snapshot {typeof(MislaidShelfSnapshotV1).FullName} of {typeof(Stub.Shelf).FullName} is declared by the Module StubModule, but Shelf changed in an application without Modules. Declare it with PublishSnapshot(wireName) and no Module.",
            thrown.Message
        );
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.shelves"));
    }

    [Fact]
    public async Task A_notification_handler_that_publishes_a_snapshot_by_hand_fails_its_delivery()
    {
        var folder = Guid.CreateVersion7();

        var (_, deliveries) = await Fixture.WaitForDeliveriesAsync(
            () => StubAsync(new WriteNoteRaisingCommand($"note {folder}", [new FolderSnapshotPublishedByHandDomainEvent(folder)])),
            TestContext.Current.CancellationToken
        );

        Assert.True(Assert.Single(deliveries).DeadLettered);
        Assert.Contains(
            $"{typeof(FolderSnapshotV1).FullName} is a Snapshot",
            await Fixture.ScalarAsync<string>("select exception_message from wolverine.wolverine_dead_letters")
        );
        Assert.Empty(FolderSnapshots(folder));
    }

    [Fact]
    public void A_contract_is_published_as_an_integration_event_or_as_one_aggregates_snapshot()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection()
                .Publish<FolderSnapshotV1>("tests.folder.v1")
                .PublishSnapshot<Folder, FolderSnapshotV1, FolderSnapshotMapper>("tests.folder.v1")
        );

        Assert.Equal(
            $"{typeof(FolderSnapshotV1).FullName} is published as an Integration Event and as the Snapshot of {typeof(Folder).FullName} mapped by {typeof(FolderSnapshotMapper).FullName}; a contract is an Integration Event or the Snapshot of one aggregate, with one mapper.",
            thrown.Message
        );
    }

    // Runs one Stub Operation on a Host of the application that also makes the declaration, and returns what it threw.
    private async Task<InvalidOperationException> WithDeclarationAsync(
        Action<IServiceCollection> declare,
        Func<IGatewayProcessor<StubModule>, Task<Result>> operation
    )
    {
        await using var host = AppFixture.ComposeHost("Development", Fixture.ConnectionString, Fixture.Logs, builder => declare(builder.Services));
        await HostFixture.StartHostAsync(host, TestContext.Current.CancellationToken);
        try
        {
            return await Assert.ThrowsAsync<InvalidOperationException>(() =>
                operation(host.Services.GetRequiredService<IGatewayProcessor<StubModule>>())
            );
        }
        finally
        {
            // Stopped, so a delivery it took is finished or handed back before the next test waits for the store.
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    // Creates the product, renames it, then renames or removes it, each in an Operation of its own, and returns the
    // envelope that carried each version's Snapshot to Inventory, oldest first.
    private async Task<Guid[]> ThreeVersionsAsync(Guid product, bool remove)
    {
        return
        [
            await ChangeProductAsync(() => CreateProductAsync(product, "Lamp")),
            await ChangeProductAsync(() => RenameProductAsync(product, "Desk lamp")),
            await ChangeProductAsync(async () =>
                remove
                    ? await Fixture.Gateway<CatalogModule>().SendAsync(new RemoveProductsCommand(["Desk lamp"]), TestContext.Current.CancellationToken)
                    : await RenameProductAsync(product, "Floor lamp")
            ),
        ];
    }

    // Runs an Operation that changes one product, waits for its deliveries, and returns the envelope of its Snapshot to
    // Inventory.
    private async Task<Guid> ChangeProductAsync(Func<Task<Result>> change)
    {
        var before = await InventoryEnvelopesAsync();
        var (changed, _) = await Fixture.WaitForDeliveriesAsync(change, TestContext.Current.CancellationToken);
        Assert.True(changed.IsSuccess);
        return Assert.Single((await InventoryEnvelopesAsync()).Except(before));
    }

    private async Task<List<Guid>> InventoryEnvelopesAsync() =>
        [
            .. (
                await Fixture.RowsAsync(
                    $"select id from wolverine.wolverine_incoming_envelopes where received_at = 'local://{InventorySubscription}/'"
                )
            ).Select(row => (Guid)row[0]),
        ];

    // What Inventory has applied, as if none of the Snapshots had ever arrived.
    private Task ForgetCopiesAsync() =>
        Fixture.RowsAsync(
            $"delete from inventory.catalog_products; delete from inventory.pouryakit_snapshot_versions; delete from inventory.pouryakit_receipts where subscription = '{InventorySubscription}'"
        );

    // The store hands the delivery out again, as recovery does for one whose Host did not finish it.
    private async Task RedeliverAsync(Guid envelope) =>
        Assert.Single(
            await Fixture.RowsAsync(
                $"update wolverine.wolverine_incoming_envelopes set status = 'Incoming', owner_id = 0 where id = '{envelope}' returning id"
            )
        );

    private async Task WaitForVersionWaitingAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (
            await Fixture.ScalarAsync<long>(
                "select count(*) from pg_stat_activity where wait_event_type = 'Lock' and query like '%pouryakit_snapshot_versions%'"
            ) < 1
        )
        {
            Assert.False(timeout.IsCancellationRequested, "The second delivery never waited on the held delivery's version.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    private async Task<object[][]> CopyAndProductAsync(Guid product) =>
        [
            .. await Fixture.RowsAsync(
                $"select copy.name, copy.version, products.version from inventory.catalog_products copy join catalog.products on products.id = copy.product_id where copy.product_id = '{product}'"
            ),
        ];

    private List<FolderSnapshotV1> FolderSnapshots(Guid folder) =>
        [.. Received.All.Select(received => received.IntegrationEvent).OfType<FolderSnapshotV1>().Where(snapshot => snapshot.FolderId == folder)];

    private async Task<Result> CreateProductAsync(Guid product, string name)
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var category = Guid.CreateVersion7();
        await catalog.SendAsync(new CreateCategoryCommand(category, $"Lamps {category}"), TestContext.Current.CancellationToken);
        return await catalog.SendAsync(new CreateProductCommand(product, category, name), TestContext.Current.CancellationToken);
    }

    private Task<Result> RenameProductAsync(Guid product, string name) =>
        Fixture.Gateway<CatalogModule>().SendAsync(new RenameProductCommand(product, name), TestContext.Current.CancellationToken);

    private Task<Result> StubAsync(Command command) =>
        Fixture.Gateway<StubModule>().SendAsync(command, TestContext.Current.CancellationToken);
}
