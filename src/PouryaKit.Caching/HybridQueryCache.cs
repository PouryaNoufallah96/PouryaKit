using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Caching;

// One HybridCache.GetOrCreateAsync per cacheable query, so concurrent misses of one key in this Host share one handler
// run; another Host runs its own. An entry is the success value as JSON in the Kit's representation, read the same way
// from this Host's memory and from a distributed cache.
internal sealed partial class HybridQueryCache(HybridCache cache, ILogger<HybridQueryCache> logger) : IQueryCache
{
    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions().UsePouryaKitJson();

    public async Task<Result<TResponse>> QueryAsync<TResponse>(
        Query<TResponse> query,
        OperationContext context,
        Func<CancellationToken, Task<Result<TResponse>>> handle,
        Action endRead,
        CancellationToken cancellationToken
    )
        where TResponse : notnull
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(endRead);
        if (query is not ICacheableQuery cacheable)
        {
            return await handle(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        string stored;
        try
        {
            // The shared run is not any one caller's to cancel, since others may be waiting on it. Without a token,
            // HybridCache runs the handler on the call that starts the run, which waits for it, so that caller's scope
            // and Operation stay alive until the run ends; every other caller waits for it too.
            stored = await cache.GetOrCreateAsync(
                Key(context, query.GetType(), cacheable.CacheKey),
                (handle, endRead),
                static async (run, ct) =>
                {
                    var result = await run.handle(ct);
                    if (result.IsFailure)
                    {
                        throw new NotStored(result.Errors);
                    }

                    // Serialized before the read ends, so its check also sees a change that serializing made, such as
                    // a deferred enumerable's.
                    var serialized = JsonSerializer.Serialize(result.Value, Json);
                    run.endRead();
                    return serialized;
                },
                new HybridCacheEntryOptions { Expiration = cacheable.CacheDuration, LocalCacheExpiration = cacheable.CacheDuration },
                cacheable.CacheTags,
                CancellationToken.None
            );
        }
        catch (NotStored failed)
        {
            return failed.Errors;
        }

        return JsonSerializer.Deserialize<TResponse>(stored, Json)!;
    }

    public IEnumerable<string> TagsInvalidatedBy(object command) =>
        command is IInvalidatesCache invalidates ? invalidates.InvalidatedCacheTags : [];

    // The Operation has committed, so a failed eviction cannot fail it: it is logged, and the tag's entries are returned
    // until they expire. Tag by tag, so a distributed cache that refuses one leaves the others evicted.
    public async Task EvictAsync(IReadOnlyCollection<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        foreach (var tag in tags)
        {
            try
            {
                await cache.RemoveByTagAsync(tag, CancellationToken.None);
            }
            catch (Exception exception)
            {
                LogEvictionFailed(logger, exception, tag);
            }
        }
    }

    // Each part escaped, so no tenant, Actor id or declared key can end its part early and match another entry's key.
    private static string Key(OperationContext context, Type query, string key) =>
        string.Join(
            '|',
            query.FullName,
            Uri.EscapeDataString(context.Tenant ?? OperationContext.NoTenant),
            context.Actor.Kind,
            Uri.EscapeDataString(context.Actor.Id),
            Uri.EscapeDataString(key)
        );

    [LoggerMessage(
        EventName = "EvictionFailed",
        Level = LogLevel.Error,
        Message = "Evicting the cache tag {Tag} failed after its Operation committed; its entries are returned until they expire"
    )]
    private static partial void LogEvictionFailed(ILogger logger, Exception exception, string tag);

    // Thrown by the factory, so HybridCache stores nothing; every caller that shared the run receives the failure.
    private sealed class NotStored(ImmutableArray<Error> errors) : Exception
    {
        public ImmutableArray<Error> Errors { get; } = errors;
    }
}
