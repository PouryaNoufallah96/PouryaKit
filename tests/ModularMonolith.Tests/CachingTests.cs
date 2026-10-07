using System.Diagnostics.Tracing;
using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Errors;
using Catalog.Products.Features.CreateProduct;
using Catalog.Products.Features.GetProduct;
using Catalog.Products.Features.RemoveProducts;
using Catalog.Products.Features.RenameProduct;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using PouryaKit.Testing;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace ModularMonolith.Tests;

// Query caching against PostgreSQL, through Gateways: Catalog's GetProduct, cached per Actor and tenant and evicted by
// RenameProduct and RemoveProducts, and Stub queries whose handlers record each run. A product renamed with plain SQL
// behind the cache's back tells a stored answer, the old name, from a read of the database, the new one.
public sealed class CachingTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private IGatewayProcessor<CatalogModule> Catalog => Fixture.Gateway<CatalogModule>();

    private IGatewayProcessor<StubModule> Stub => Fixture.Gateway<StubModule>();

    private CachedQueryRuns Runs => Fixture.Host.Services.GetRequiredService<CachedQueryRuns>();

    [Fact]
    public async Task An_entry_answers_only_its_own_actor_and_tenant()
    {
        var lamp = await CreateProductAsync("Lamp");
        Assert.Equal("Lamp", await NameAsync(lamp, As("ada")));
        await RenameBehindTheCacheAsync(lamp, "Shadow");

        Assert.Equal("Shadow", await NameAsync(lamp, As("bob")));
        Assert.Equal("Shadow", await NameAsync(lamp, new OperationContext(Actor.System("ada"))));
        Assert.Equal("Shadow", await NameAsync(lamp, As("ada", "north")));
        await RenameBehindTheCacheAsync(lamp, "Glow");
        Assert.Equal("Glow", await NameAsync(lamp, As("ada", "south")));

        Assert.Equal("Lamp", await NameAsync(lamp, As("ada")));
        Assert.Equal("Shadow", await NameAsync(lamp, As("bob")));
        Assert.Equal("Shadow", await NameAsync(lamp, As("ada", "north")));
    }

    [Fact]
    public async Task A_failed_result_is_never_stored()
    {
        var lamp = Guid.CreateVersion7();

        var missing = await Catalog.QueryAsync(new GetProductQuery(lamp), As("ada"), TestContext.Current.CancellationToken);
        await CreateProductAsync("Lamp", lamp);

        Assert.Equal(ProductErrors.NotFound, Assert.Single(missing.Errors));
        Assert.Equal("Lamp", await NameAsync(lamp, As("ada")));
    }

    [Fact]
    public async Task Two_concurrent_identical_queries_in_one_host_run_the_handler_once()
    {
        var text = $"note {Guid.CreateVersion7()}";
        await Stub.SendAsync(new WriteTalliedNoteCommand(text, Reject: false), TestContext.Current.CancellationToken);
        await Stub.SendAsync(new WriteTalliedNoteCommand(text, Reject: false), TestContext.Current.CancellationToken);
        using var joins = new StampedeJoins();
        var hold = new Hold();

        var first = Stub.QueryAsync(new CountNotesQuery(text, hold), As("ada"), TestContext.Current.CancellationToken);
        await hold.Changed.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = Stub.QueryAsync(new CountNotesQuery(text), As("ada"), TestContext.Current.CancellationToken);
        // The second joins the first's run, or, run on its own, completes while the first is held.
        await Task.WhenAny(joins.Joined, second).WaitAsync(TestContext.Current.CancellationToken);
        hold.Released.SetResult();

        Assert.Equal(2, (await first).Value);
        Assert.Equal(2, (await second).Value);
        Assert.Equal([2], Runs.Of(text));
    }

    [Fact]
    public async Task A_committed_rename_evicts_every_actors_entry_and_a_warm_read_answers_the_new_name()
    {
        var lamp = await CreateProductAsync("Lamp");
        Assert.Equal("Lamp", await NameAsync(lamp, As("ada")));
        Assert.Equal("Lamp", await NameAsync(lamp, As("bob")));
        await RenameBehindTheCacheAsync(lamp, "Shadow");
        Assert.Equal("Lamp", await NameAsync(lamp, As("bob")));

        var renamed = await Catalog.SendAsync(new RenameProductCommand(lamp, "Desk lamp"), As("ada"), TestContext.Current.CancellationToken);

        Assert.True(renamed.IsSuccess);
        Assert.Equal("Desk lamp", await NameAsync(lamp, As("ada")));
        Assert.Equal("Desk lamp", await NameAsync(lamp, As("bob")));
    }

    [Fact]
    public async Task After_a_committed_removal_a_warm_read_is_not_found_for_every_actor()
    {
        var lamp = await CreateProductAsync("Lamp");
        Assert.Equal("Lamp", await NameAsync(lamp, As("ada")));
        Assert.Equal("Lamp", await NameAsync(lamp, As("bob")));
        await RenameBehindTheCacheAsync(lamp, "Shadow");
        Assert.Equal("Lamp", await NameAsync(lamp, As("ada")));

        var removed = await Catalog.SendAsync(new RemoveProductsCommand(["Shadow"]), TestContext.Current.CancellationToken);

        Assert.Equal(1, removed.Value);
        foreach (var actor in new[] { As("ada"), As("bob") })
        {
            var read = await Catalog.QueryAsync(new GetProductQuery(lamp), actor, TestContext.Current.CancellationToken);
            Assert.Equal(ProductErrors.NotFound, Assert.Single(read.Errors));
        }
    }

    [Fact]
    public async Task A_rolled_back_rename_evicts_nothing_so_the_valid_entry_survives()
    {
        var lamp = await CreateProductAsync("Lamp");
        await CreateProductAsync("Desk");
        Assert.Equal("Lamp", await NameAsync(lamp, As("ada")));
        await RenameBehindTheCacheAsync(lamp, "Shadow");

        var refused = await Catalog.SendAsync(new RenameProductCommand(lamp, "Desk"), As("ada"), TestContext.Current.CancellationToken);

        Assert.Equal(ProductErrors.NameInUse, Assert.Single(refused.Errors));
        Assert.Equal("Lamp", await NameAsync(lamp, As("ada")));
    }

    [Fact]
    public async Task A_query_nested_in_a_write_unit_of_work_that_rolls_back_leaves_no_entry()
    {
        var text = $"tally {Guid.CreateVersion7()}";

        var refused = await Stub.SendAsync(new TallyReadThenRefuseCommand(text), As("ada"), TestContext.Current.CancellationToken);
        var read = await Stub.QueryAsync(new ReadTallyQuery(text), As("ada"), TestContext.Current.CancellationToken);

        Assert.Equal(TallyReadThenRefuseCommandHandler.Refused, Assert.Single(refused.Errors));
        Assert.Equal(0, read.Value);
        // Inside the Unit of Work the query saw the uncommitted tally; outside, after the rollback, it ran again.
        Assert.Equal([1, 0], Runs.Of(text));
    }

    [Fact]
    public async Task A_cached_querys_handler_runs_inside_its_operation_so_a_continuation_it_creates_is_trusted()
    {
        var trusted = await Stub.QueryAsync(new ContinuesTrustedQuery(Guid.CreateVersion7()), As("ada"), TestContext.Current.CancellationToken);

        Assert.True(trusted.Value);
    }

    [Fact]
    public async Task A_commit_stands_and_evicts_this_hosts_entry_when_the_distributed_cache_refuses_the_eviction()
    {
        var lamp = await CreateProductAsync("Lamp");
        await using var host = AppFixture.ComposeHost(
            "Development",
            Fixture.ConnectionString,
            Fixture.Logs,
            builder => builder.Services.AddSingleton<IDistributedCache, RefusingDistributedCache>()
        );
        await HostFixture.StartHostAsync(host, TestContext.Current.CancellationToken);
        var catalog = host.Services.GetRequiredService<IGatewayProcessor<CatalogModule>>();
        Assert.Equal("Lamp", await NameAsync(catalog, lamp, As("ada")));
        await RenameBehindTheCacheAsync(lamp, "Shadow");
        Assert.Equal("Lamp", await NameAsync(catalog, lamp, As("ada")));

        var renamed = await catalog.SendAsync(new RenameProductCommand(lamp, "Desk lamp"), As("ada"), TestContext.Current.CancellationToken);
        var read = await NameAsync(catalog, lamp, As("ada"));
        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(renamed.IsSuccess);
        Assert.Equal("Desk lamp", read);
        Assert.Contains(
            Fixture.Logs.Entries,
            entry => entry.Level == LogLevel.Error && entry.Message.StartsWith($"Evicting the cache tag product:{lamp} failed", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task With_redis_a_second_host_reads_the_entry_the_first_stored_without_running_its_handler()
    {
        await using var redis = new RedisBuilder("redis:8.10.1").Build();
        await redis.StartAsync(TestContext.Current.CancellationToken);
        string[] arguments = [$"--ConnectionStrings:cache={redis.GetConnectionString()}"];
        await using var first = AppFixture.ComposeHost("Development", Fixture.ConnectionString, Fixture.Logs, arguments: arguments);
        await using var second = AppFixture.ComposeHost("Development", Fixture.ConnectionString, Fixture.Logs, arguments: arguments);
        var lamp = await CreateProductAsync("Lamp");

        Assert.Equal("Lamp", await NameAsync(Gateway(first), lamp, As("ada")));
        await WaitUntilStoredAsync(redis);
        await RenameBehindTheCacheAsync(lamp, "Shadow");

        Assert.Equal("Lamp", await NameAsync(Gateway(second), lamp, As("ada")));
        Assert.Equal("Shadow", await NameAsync(Gateway(second), lamp, As("bob")));
    }

    private static OperationContext As(string person, string? tenant = null) => new(Actor.Person(person), tenant);

    private static IGatewayProcessor<CatalogModule> Gateway(IHost host) =>
        host.Services.GetRequiredService<IGatewayProcessor<CatalogModule>>();

    private Task<string> NameAsync(Guid product, OperationContext context) => NameAsync(Catalog, product, context);

    private static async Task<string> NameAsync(IGatewayProcessor<CatalogModule> catalog, Guid product, OperationContext context) =>
        (await catalog.QueryAsync(new GetProductQuery(product), context, TestContext.Current.CancellationToken)).Value.Name;

    private async Task<Guid> CreateProductAsync(string name, Guid? id = null)
    {
        var category = Guid.CreateVersion7();
        var product = id ?? Guid.CreateVersion7();
        await Catalog.SendAsync(new CreateCategoryCommand(category, $"For {name}"), TestContext.Current.CancellationToken);
        var created = await Catalog.SendAsync(new CreateProductCommand(product, category, name), TestContext.Current.CancellationToken);
        Assert.True(created.IsSuccess);
        return product;
    }

    private Task RenameBehindTheCacheAsync(Guid product, string name) =>
        Fixture.RowsAsync($"update catalog.products set name = '{name}' where id = '{product}'");

    // HybridCache answers its first caller before its write to the distributed cache completes.
    private static async Task WaitUntilStoredAsync(RedisContainer redis)
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.GetConnectionString());
        var server = connection.GetServer(connection.GetEndPoints()[0]);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (await server.DatabaseSizeAsync() == 0)
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    // Signals when a caller joins a HybridCache run already under way in this process: the single flight of one instance.
    private sealed class StampedeJoins : EventListener
    {
        private readonly TaskCompletionSource _joined = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Joined => _joined.Task;

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Microsoft-Extensions-HybridCache")
            {
                EnableEvents(eventSource, EventLevel.Verbose);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName == "StampedeJoin")
            {
                _joined.TrySetResult();
            }
        }
    }

    // A distributed cache that is down: every call fails.
    private sealed class RefusingDistributedCache : IDistributedCache
    {
        private static InvalidOperationException Down() => new("The distributed cache is down.");

        public byte[]? Get(string key) => throw Down();

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromException<byte[]?>(Down());

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw Down();

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
            Task.FromException(Down());

        public void Refresh(string key) => throw Down();

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.FromException(Down());

        public void Remove(string key) => throw Down();

        public Task RemoveAsync(string key, CancellationToken token = default) => Task.FromException(Down());
    }
}
