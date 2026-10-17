using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Caching;
using PouryaKit.Cqrs;
using PouryaKit.Persistence.Postgres;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

// A stored idempotency key and a distributed cache entry outlive the build that wrote them, so the way each names its
// command or query stays fixed: the type's full name, then its assembly's simple name, never the assembly-qualified
// name. The literals are the keys as the key table and the distributed cache hold them.
public sealed class TypeKeyTests(AppFixture app) : HostTest<AppFixture>(app)
{
    [Fact]
    public async Task The_idempotency_key_and_the_cache_key_name_a_type_by_its_full_name_and_its_assemblys_simple_name()
    {
        var distributed = new RecordingDistributedCache();
        await using var services = Services(distributed);
        var context = new OperationContext(Actor.Person("ada"), "north");
        // The cache key names the UI culture the query runs in (ticket #125); the test's own flow sets it.
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");

        await RunAsync(services, context with { RequestId = "type-key" }, dispatcher => dispatcher.SendAsync(new NameHandlingAssemblyCommand(), TestContext.Current.CancellationToken));
        await RunAsync(services, context, dispatcher => dispatcher.QueryAsync(new HandlingAssemblyQuery("lamp"), TestContext.Current.CancellationToken));

        Assert.Equal(
            "ModularMonolith.Tests.Stub.NameHandlingAssemblyCommand, ModularMonolith.Tests",
            await Fixture.ScalarAsync<string>("select command from stub.pouryakit_idempotency_keys where request_id = 'type-key'")
        );
        // HybridCache answers before its distributed write completes.
        await distributed.WrittenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["ModularMonolith.Tests.Stub.HandlingAssemblyQuery%2C%20ModularMonolith.Tests|north|Person|ada|en|lamp"], distributed.Keys);
    }

    // Each Operation in a scope of its own, as an application without Modules opens one.
    private static async Task RunAsync<TResult>(ServiceProvider services, OperationContext context, Func<IDispatcher, Task<TResult>> run)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(context);
        _ = await run(scope.ServiceProvider.GetRequiredService<IDispatcher>());
    }

    private ServiceProvider Services(IDistributedCache distributed)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection([new("ConnectionStrings:app", Fixture.ConnectionString)])
                .Build()
        );
        services.UsePouryaKitPostgres<StubDbContext>(connectionName: "app", schema: "stub");
        services.AddPouryaKitCqrs(typeof(StubDbContext).Assembly);
        services.AddSingleton(distributed);
        services.AddPouryaKitCaching();
        return services.BuildServiceProvider();
    }

    // Keeps entries in memory and records the key of each one written.
    private sealed class RecordingDistributedCache : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> _entries = new();
        private readonly TaskCompletionSource _written = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyCollection<string> Keys => [.. _entries.Keys];

        public Task WrittenAsync(CancellationToken cancellationToken) => _written.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        public byte[]? Get(string key) => _entries.GetValueOrDefault(key);

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            _entries[key] = value;
            _written.TrySetResult();
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public void Refresh(string key) { }

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Remove(string key) => _entries.TryRemove(key, out _);

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }
    }
}
