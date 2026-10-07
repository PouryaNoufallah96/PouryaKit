namespace PouryaKit.Caching;

/// <summary>
/// Opts a query into the query cache. In a Host that calls <c>AddPouryaKitCaching</c>, its success is stored for
/// <see cref="CacheDuration"/> under <see cref="CacheKey"/> and returned to a repeat of the same query, without running
/// its handler. Its response is stored as JSON in the Kit's one representation, so it round-trips through it. An entry
/// belongs to one Actor and tenant: the Kit adds the Operation's Actor, tenant and the query's type to the key, so the
/// key names only what the query asks. A failed Result is never stored. The query runs uncached inside a write Unit of
/// Work, where it may see uncommitted state, and in a Host without caching. Declare it on a <c>Query&lt;T&gt;</c>.
/// </summary>
public interface ICacheableQuery
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
