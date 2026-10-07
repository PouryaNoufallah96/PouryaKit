namespace PouryaKit.Cqrs;

/// <summary>
/// Inherited by a Kit contract that, on a query, asks the query cache to answer it, as Caching's
/// <c>ICacheableQuery</c> does; applications do not implement it directly. A Host whose registered query handlers serve
/// such a query fails when it starts when no <see cref="IQueryCache"/> is registered, naming <c>AddPouryaKitCaching()</c>,
/// so a cacheable query never runs uncached without a word (ADR 0002). It is a Cqrs type because the check runs in every
/// Host and Cqrs never references the cache's package.
/// </summary>
public interface IRequiresQueryCache;
