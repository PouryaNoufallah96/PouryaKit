using PouryaKit.Cqrs;

namespace PouryaKit.Caching;

/// <summary>
/// Opts a query into the query cache. A Host that registers its handler calls <c>AddPouryaKitCaching()</c>, or fails
/// when it is built, naming the call (<see cref="IRequiresQueryCache"/>): its success is stored for
/// <see cref="CacheDuration"/> under <see cref="CacheKey"/> and returned to a repeat of the same query, without running
/// its handler. Its response is stored as JSON in the Kit's one representation, so it round-trips through it. An entry
/// belongs to one Actor and tenant: the Kit adds the Operation's Actor, tenant and the query's type, by its full name
/// and its assembly's simple name, to the key, so the key names only what the query asks. A failed Result is never
/// stored, and neither is a success whose handler, or whose serialization, changed tracked state, which throws. A
/// success whose run was in flight when a commit in this Host evicted one of its <see cref="CacheTags"/> is not stored
/// either: every caller that shared the run receives it, and the next read runs the handler again. Once its handler
/// starts it runs to completion, ignoring the caller's cancellation, because other callers of the same query may be
/// waiting on it; every caller waits for it. It uses the cache only as its Operation's outermost query: sent from a
/// command's handler, an authorizer, a validator or another query, it may read a change they made, so it runs
/// uncached, as it does in an Operation that has already failed. Declare it on a <c>Query&lt;T&gt;</c>.
/// </summary>
public interface ICacheableQuery : IRequiresQueryCache
{
    /// <summary>What the query asks, such as the id it reads; unique among the queries of its type.</summary>
    string CacheKey { get; }

    /// <summary>How long a stored success is returned, in this Host's memory and in a distributed cache alike.</summary>
    TimeSpan CacheDuration { get; }

    /// <summary>
    /// The resources the answer reads, such as <c>product:{id}</c>. A tag spans Actors and tenants: a committed command
    /// that declares it, with <see cref="IInvalidatesCache"/>, evicts every Actor's entry.
    /// </summary>
    IReadOnlyCollection<string> CacheTags { get; }
}
