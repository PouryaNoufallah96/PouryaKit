using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Reflection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using PouryaKit.Persistence.EFCore;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

public sealed class ReadOnlyStateTests(AppFixture app) : HostTest<AppFixture>(app)
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task A_failed_or_throwing_query_cannot_mutate_a_held_or_newly_loaded_aggregate(bool held, bool queryThrows)
    {
        var folder = await CreateFolderAsync();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync(new ReadFolderCommand(folder, ReadingPoint.Query, ReadingChange.Root, Held: held, QueryThrows: queryThrows)));

        Assert.StartsWith("A query changed a Folder the Operation saves", thrown.Message);
        await AssertUnchangedAsync();
    }

    [Theory]
    [InlineData(ReadingPoint.Query)]
    [InlineData(ReadingPoint.Authorizer)]
    [InlineData(ReadingPoint.Validator)]
    [InlineData(ReadingPoint.OnlyReading)]
    public async Task A_caught_read_violation_still_rolls_the_outer_command_back(ReadingPoint point)
    {
        var folder = await CreateFolderAsync();

        var result = await SendAsync(new ReadFolderCommand(folder, point, ReadingChange.Root, Swallow: true));

        Assert.Equal("Operation.ReadingChangedState", Assert.Single(result.Errors).Code);
        await AssertUnchangedAsync();
    }

    [Theory]
    [InlineData(ReadingChange.Child)]
    [InlineData(ReadingChange.AddChild)]
    [InlineData(ReadingChange.OwnedValue)]
    [InlineData(ReadingChange.DomainEvent)]
    public async Task A_query_cannot_change_a_child_an_owned_value_or_pending_domain_events(ReadingChange change)
    {
        var folder = await CreateFolderAsync();

        var result = await SendAsync(new ReadFolderCommand(folder, ReadingPoint.Query, change, Swallow: true));

        Assert.Equal("Operation.ReadingChangedState", Assert.Single(result.Errors).Code);
        await AssertUnchangedAsync();
        Assert.Equal([["first page"], ["second page"]], await Fixture.RowsAsync("select text from stub.pages order by text"));
    }

    [Theory]
    [InlineData(ReadingChange.AddChild)]
    [InlineData(ReadingChange.RemoveChild)]
    public async Task A_query_cannot_change_navigation_membership_when_automatic_change_detection_is_disabled(ReadingChange change)
    {
        var folder = await CreateFolderAsync();

        var result = await SendAsync(new ReadFolderWithoutAutomaticDetectionCommand(folder, change));

        Assert.Equal("Operation.ReadingChangedState", Assert.Single(result.Errors).Code);
        await AssertUnchangedAsync();
        Assert.Equal([["first page"], ["second page"]], await Fixture.RowsAsync("select text from stub.pages order by text"));
    }

    [Fact]
    public async Task An_innocent_failed_query_accepts_pending_navigation_changes_and_new_reads_when_automatic_detection_is_disabled()
    {
        var folder = await CreateFolderAsync();
        var additional = await CreateFolderAsync();

        var result = await SendAsync(new ReadFolderWithoutAutomaticDetectionCommand(folder, ReadingChange.None, additional));

        Assert.True(result.IsSuccess);
        Assert.Equal([["first page"], ["legitimate pending page"], ["second page"]], await Fixture.RowsAsync($"select text from stub.pages where folder_id = '{folder}' order by text"));
        Assert.Equal(2L, await Fixture.ScalarAsync<long>($"select version from stub.folders where id = '{folder}'"));
        Assert.Equal(1L, await Fixture.ScalarAsync<long>($"select version from stub.folders where id = '{additional}'"));
    }

    [Theory]
    [InlineData(ReadingPoint.Authorizer)]
    [InlineData(ReadingPoint.Validator)]
    public async Task An_outer_command_check_uses_the_owning_modules_guard_before_its_transaction_starts(ReadingPoint point)
    {
        var folder = await CreateFolderAsync();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync(new ReadFolderOuterCheckCommand(folder, point)));

        Assert.StartsWith("An authorizer or a validator changed a Label the Operation saves", thrown.Message);
        await AssertUnchangedAsync();
    }

    [Fact]
    public async Task A_query_cannot_mutate_a_value_in_place_even_when_it_was_already_changed_by_the_command()
    {
        await Fixture.RowsAsync("insert into stub.shelves (aisle, bay, labels, size_width, size_depth, version) values ('aisle', 'bay', ARRAY['original'], 1, 1, 1)");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync(new ReadShelfLabelsCommand("aisle", "bay")));

        Assert.StartsWith("A query changed a Shelf the Operation saves", thrown.Message);
        Assert.Equal([["original", 1]], await Fixture.RowsAsync("select labels[1], cardinality(labels) from stub.shelves"));
        Assert.Equal(1L, await Fixture.ScalarAsync<long>("select version from stub.shelves"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_query_cannot_replace_or_reorder_the_same_number_of_pending_domain_events(bool reverse)
    {
        Assert.True((await SendAsync(new WriteNoteCommand("events"))).IsSuccess);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync(new ReadNoteEventsCommand("events", reverse)));

        Assert.StartsWith("A query raised a Domain Event on a Note, or changed its pending Domain Events", thrown.Message);
        Assert.Equal(1L, await Fixture.ScalarAsync<long>("select version from stub.notes"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_innocent_failed_query_keeps_pending_command_changes_and_can_track_an_additional_read(bool held)
    {
        var folder = await CreateFolderAsync();

        var result = await SendAsync(new ReadFolderCommand(folder, ReadingPoint.Query, ReadingChange.None, Held: held));

        Assert.True(result.IsSuccess);
        Assert.Equal(1L, await Fixture.ScalarAsync<long>("select count(*) from stub.notes"));
        Assert.Equal(held ? "blue" : "grey", await Fixture.ScalarAsync<string>("select label_colour from stub.folders"));
        Assert.Equal(held ? 2L : 1L, await Fixture.ScalarAsync<long>("select version from stub.folders"));
    }

    [Fact]
    public async Task A_standalone_query_uses_its_modules_guard_and_leaves_persisted_state_unchanged()
    {
        var folder = await CreateFolderAsync();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => Fixture.Gateway<StubModule>().QueryAsync(new ReadFolderQuery(folder, null, ReadingChange.Child, false), TestContext.Current.CancellationToken));

        Assert.StartsWith("A query changed a Page the Operation saves", thrown.Message);
        await AssertUnchangedAsync();
    }

    [Fact]
    public async Task A_query_only_module_runs_without_registering_persistence()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.AddPouryaKitModules(modules => modules.Add<QueryOnlyReadingModule>());
        using var host = builder.Build();

        var result = await host.Services.GetRequiredService<IGatewayProcessor<QueryOnlyReadingModule>>().QueryAsync(new ConstantReadQuery(), new OperationContext(GatewayTestExtensions.TestActor), TestContext.Current.CancellationToken);

        Assert.Equal("read without persistence", result.Value);
    }

    [Fact]
    public async Task A_query_that_reads_no_context_can_run_in_an_unbegun_module_host_scope()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.AddPouryaKitModules(modules => modules.Add<QueryOnlyReadingModule>());
        using var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().QueryAsync(new ConstantReadQuery(), TestContext.Current.CancellationToken);

        Assert.Equal("read without persistence", result.Value);
    }

    [Fact]
    public async Task A_snapshot_batch_does_not_copy_the_whole_tracker_for_each_mapper()
    {
        await using var scope = Fixture.Host.Services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(new OperationContext(GatewayTestExtensions.TestActor), typeof(StubModule));
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext!;
        var mappers = scope.ServiceProvider.GetRequiredService<ISnapshotMappers>();
        var build = Assembly.Load("PouryaKit.Messaging.Wolverine.EFCore").GetType("PouryaKit.Messaging.Wolverine.EFCore.SnapshotStep")!.GetMethod("Build")!;
        var warm = Stub.Shelf.Put("warm", "bay");
        db.Add(warm);
        _ = build.Invoke(null, [db, new[] { db.Entry(warm) }, mappers]);
        db.ChangeTracker.Clear();
        db.AddRange(Enumerable.Range(0, 1000).Select(index => Stub.Shelf.Put($"aisle {index}", "bay", "original")));
        var changed = db.ChangeTracker.Entries().ToList();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var snapshots = (IReadOnlyList<(object Snapshot, string AggregateId, long Version)>)build.Invoke(null, [db, changed, mappers])!;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(1000, snapshots.Count);
        Assert.True(allocated < 20_000_000, $"A batch of 1000 Snapshots allocated {allocated:N0} bytes.");
        TestContext.Current.TestOutputHelper?.WriteLine($"1000 Snapshots allocated {allocated:N0} bytes.");
    }

    [Theory]
    [InlineData(ComplexReadingChange.Scalar, true)]
    [InlineData(ComplexReadingChange.NestedReference, true)]
    [InlineData(ComplexReadingChange.NestedCollection, true)]
    [InlineData(ComplexReadingChange.Add, true)]
    [InlineData(ComplexReadingChange.Remove, true)]
    [InlineData(ComplexReadingChange.Reverse, true)]
    [InlineData(ComplexReadingChange.Replace, true)]
    [InlineData(ComplexReadingChange.Nothing, false)]
    [InlineData(ComplexReadingChange.Nothing, true)]
    public async Task Complex_collection_reads_compare_nested_values_and_collection_membership_on_postgresql(ComplexReadingChange change, bool held)
    {
        var id = Guid.CreateVersion7();
        const string original = "[{\"Text\":\"one\",\"Detail\":{\"Number\":1},\"Children\":[{\"Text\":\"first\"}]},{\"Text\":\"two\",\"Detail\":{\"Number\":2},\"Children\":[]}]";
        try
        {
            await Fixture.RowsAsync("create table if not exists stub.read_collections (id uuid primary key, version bigint not null, parts jsonb not null)");
            await Fixture.RowsAsync($"insert into stub.read_collections (id, version, parts) values ('{id}', 1, '{original}')");
            var services = new ServiceCollection().AddLogging().AddPouryaKitCqrs();
            services.AddDbContext<ReadCollectionDbContext>(options => options.UseNpgsql(Fixture.ConnectionString));
            services.AddPouryaKitEfCore<ReadCollectionDbContext>();
            services.AddScoped<CommandHandler<ReadCollectionCommand>, ReadCollectionCommandHandler>();
            services.AddScoped<QueryHandler<ReadCollectionQuery, bool>, ReadCollectionQueryHandler>();
            await using var host = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            await using var scope = host.CreateAsyncScope();

            var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync(new ReadCollectionCommand(id, change, held), TestContext.Current.CancellationToken);

            if (change == ComplexReadingChange.Nothing)
            {
                Assert.True(result.IsSuccess);
            }
            else
            {
                Assert.Equal("Operation.ReadingChangedState", Assert.Single(result.Errors).Code);
            }

            var expected = change == ComplexReadingChange.Nothing && held ? original.Replace("two", "legitimate pending change", StringComparison.Ordinal) : original;
            var version = change == ComplexReadingChange.Nothing && held ? 2 : 1;
            Assert.True(await Fixture.ScalarAsync<bool>($"select parts = '{expected}'::jsonb and version = {version} from stub.read_collections where id = '{id}'"));
        }
        finally
        {
            await using var connection = new NpgsqlConnection(Fixture.ConnectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using var cleanup = new NpgsqlCommand("drop table if exists stub.read_collections", connection);
            await cleanup.ExecuteNonQueryAsync(CancellationToken.None);
        }

        Assert.True(await Fixture.ScalarAsync<bool>("select to_regclass('stub.read_collections') is null"));
    }

    private async Task<Guid> CreateFolderAsync()
    {
        var folder = Guid.CreateVersion7();
        Assert.True((await SendAsync(new CreateFolderCommand(folder))).IsSuccess);
        return folder;
    }

    private async Task AssertUnchangedAsync()
    {
        Assert.Equal([["drafts", "grey", 1L]], await Fixture.RowsAsync("select name, label_colour, version from stub.folders"));
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.notes"));
    }

    private Task<PouryaKit.Domain.Result> SendAsync(Command command) => Fixture.Gateway<StubModule>().SendAsync(command, TestContext.Current.CancellationToken);

    public sealed class QueryOnlyReadingModule : IModuleDefinition
    {
        // Its one handler by hand: AddPouryaKitCqrs() on its builder would scan this whole test assembly.
        public void Configure(ModuleBuilder module) =>
            module.AddScoped<QueryHandler<ConstantReadQuery, string>, ConstantReadQueryHandler>();
    }
}
