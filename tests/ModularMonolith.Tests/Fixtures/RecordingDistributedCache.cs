using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Distributed;

namespace ModularMonolith.Tests.Fixtures;

/// <summary>A distributed cache that keeps its entries in memory and records the key of each one written.</summary>
public sealed class RecordingDistributedCache : IDistributedCache
{
    private readonly ConcurrentDictionary<string, byte[]> _entries = new();
    private readonly ConcurrentQueue<string> _written = new();

    /// <summary>The keys it holds.</summary>
    public IReadOnlyCollection<string> Keys => [.. _entries.Keys];

    /// <summary>
    /// The key of each write, in order, a key written twice included twice; HybridCache also writes the time it
    /// invalidated a tag, under a key of its own.
    /// </summary>
    public IReadOnlyList<string> Written => [.. _written];

    /// <summary>
    /// Completes once a key <paramref name="written"/> accepts is written, within 10 seconds; HybridCache answers before
    /// its distributed write completes.
    /// </summary>
    public async Task WrittenAsync(Func<string, bool> written, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!_written.Any(written))
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    public byte[]? Get(string key) => _entries.GetValueOrDefault(key);

    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
        Task.FromResult(Get(key));

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        _entries[key] = value;
        _written.Enqueue(key);
    }

    public Task SetAsync(
        string key,
        byte[] value,
        DistributedCacheEntryOptions options,
        CancellationToken token = default
    )
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
