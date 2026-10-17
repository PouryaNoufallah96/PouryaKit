using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
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

    // HybridCache's tag that evicts every entry.
    private const string WildcardTag = "*";

    // This Host's handler runs in flight, each only while its factory runs, so the record never outgrows them.
    private readonly ConcurrentDictionary<RunningRead, byte> _running = new();

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
        var tags = cacheable.CacheTags;
        string stored;
        try
        {
            // The shared run is not any one caller's to cancel, since others may be waiting on it. Without a token,
            // HybridCache runs the handler on the call that starts the run, which waits for it, so that caller's scope
            // and Operation stay alive until the run ends; every other caller waits for it too.
            stored = await cache.GetOrCreateAsync(
                Key(context, query.GetType(), cacheable.CacheKey),
                (running: _running, tags, handle, endRead),
                static async (run, ct) =>
                {
                    // HybridCache stamps an entry when its run starts and stamps it again when the factory returns if one
                    // of its tags was evicted meanwhile, so an answer read before a commit would outlive the commit's
                    // eviction. Registered before the handler reads, the run is marked by every eviction committed after.
                    var read = new RunningRead(run.tags);
                    run.running.TryAdd(read, 0);
                    try
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
                        // Checked last: an eviction landing between this check and HybridCache's own store is not seen,
                        // and a caller that joined after the commit still receives this answer, for that call only.
                        if (read.Evicted)
                        {
                            throw new NotStored(serialized);
                        }

                        return serialized;
                    }
                    finally
                    {
                        run.running.TryRemove(read, out _);
                    }
                },
                new HybridCacheEntryOptions { Expiration = cacheable.CacheDuration, LocalCacheExpiration = cacheable.CacheDuration },
                tags,
                CancellationToken.None
            );
        }
        catch (NotStored refused)
        {
            if (refused.Answer is not { } answer)
            {
                return refused.Errors;
            }

            stored = answer;
        }

        return JsonSerializer.Deserialize<TResponse>(stored, Json)!;
    }

    public IEnumerable<string> TagsInvalidatedBy(object command) =>
        command is IInvalidatesCache invalidates ? invalidates.InvalidatedCacheTags : [];

    // The Operation has committed, so a failed eviction cannot fail it: it is logged, and the tag's entries are returned
    // until they expire. Tag by tag, so a distributed cache that refuses one leaves the others evicted. A run in flight
    // on one of the tags, or on any tag when they include HybridCache's wildcard, which evicts every entry, may have
    // read before the commit, so it is marked first and stores nothing; a run that starts later reads after the commit.
    public async Task EvictAsync(IReadOnlyCollection<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        foreach (var tag in tags)
        {
            foreach (var (read, _) in _running)
            {
                if (tag == WildcardTag || read.Tags.Contains(tag))
                {
                    read.Evicted = true;
                }
            }
        }

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

    // Each part escaped, so no assembly name, tenant, Actor id, culture or declared key can end its part early and match
    // another entry's key. The query is named as every Kit key names a type (OperationTypes.KeyName). The current UI
    // culture is part of the key, so a localized answer is stored per culture; a Host without localization runs in one.
    private static string Key(OperationContext context, Type query, string key) =>
        string.Join(
            '|',
            Uri.EscapeDataString(OperationTypes.KeyName(query)),
            Uri.EscapeDataString(context.Tenant ?? OperationContext.NoTenant),
            context.Actor.Kind,
            Uri.EscapeDataString(context.Actor.Id),
            Uri.EscapeDataString(CultureInfo.CurrentUICulture.Name),
            Uri.EscapeDataString(key)
        );

    [LoggerMessage(
        EventName = "EvictionFailed",
        Level = LogLevel.Error,
        Message = "Evicting the cache tag {Tag} failed after its Operation committed; its entries are returned until they expire"
    )]
    private static partial void LogEvictionFailed(ILogger logger, Exception exception, string tag);

    // A handler run in flight with the tags its answer carries, marked once a commit evicts one of them.
    private sealed class RunningRead(IReadOnlyCollection<string> tags)
    {
        public IReadOnlyCollection<string> Tags { get; } = tags;

        public bool Evicted
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }
    }

    // Thrown by the factory, so HybridCache stores nothing; every caller that shared the run receives it: the failure,
    // or the answer of a run a commit evicted one of its tags during, which each caller reads back as from an entry.
    private sealed class NotStored : Exception
    {
        public NotStored(ImmutableArray<Error> errors) => Errors = errors;

        public NotStored(string answer) => Answer = answer;

        public ImmutableArray<Error> Errors { get; }

        public string? Answer { get; }
    }
}
