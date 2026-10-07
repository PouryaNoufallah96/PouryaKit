# Caching

`PouryaKit.Caching`: a query opts in to having its successes cached, per Actor and tenant, over `HybridCache`, and a command declares the cache tags it invalidates, evicted once its Operation commits. Namespace `PouryaKit.Caching`. It references `PouryaKit.Cqrs` and `Microsoft.Extensions.Caching.Hybrid` only. Redis, or any other distributed cache, is the application's own `IDistributedCache` registration; the Kit never references one.

- [Registering](#registering)
- [A cacheable query](#a-cacheable-query)
- [A command that invalidates](#a-command-that-invalidates)
- [What the Kit guarantees](#what-the-kit-guarantees)
- [Limits](#limits)
- [Testing](#testing)

## Registering

On the Host:

```csharp
using PouryaKit.Caching;

builder.Services.AddPouryaKitCaching();
// Optional: share entries between the application's Hosts. Microsoft.Extensions.Caching.StackExchangeRedis.
if (builder.Configuration.GetConnectionString("cache") is { } redis)
{
    builder.Services.AddStackExchangeRedisCache(options => options.Configuration = redis);
}
```

- `AddPouryaKitCaching` fills the dispatcher's query cache slot (ADR 0002) and registers `HybridCache` unless one is registered. A second call adds nothing. It registers no pipeline step an application could add to.
- A Host without it runs every query, cacheable or not, as before: the slot costs one null check.
- Without a distributed cache, entries live in each Host's memory. With one, `HybridCache` writes each entry there too, and another Host reads it.

## A cacheable query

A Module references `PouryaKit.Caching` for the contracts and declares the key, the duration and the tags on the query, as the Proving Sample's `GetProduct` does:

```csharp
using PouryaKit.Caching;
using PouryaKit.Cqrs;

internal sealed record GetProductQuery(Guid ProductId) : Query<ProductDto>, ICacheableQuery
{
    public static string ProductTag(Guid productId) => $"product:{productId}";

    public const string ProductsTag = "products";

    public string CacheKey => ProductId.ToString();

    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);

    public IReadOnlyCollection<string> CacheTags => [ProductTag(ProductId), ProductsTag];
}
```

- `CacheKey` names only what the query asks. The Kit adds the query's full type name, the Operation's tenant (an Operation without one as `OperationContext.NoTenant`) and its Actor, by kind and id, so an entry answers only the Actor and tenant that stored it.
- `CacheTags` name the resources the answer reads. A tag spans Actors and tenants: one eviction reaches every Actor's entry.
- The response is stored as JSON in the Kit's representation (`UsePouryaKitJson`), so it must round-trip through it; every answer, the first included, is read back from that JSON.
- The handler is written as for any query. It runs inside its Operation as usual, whichever thread `HybridCache` runs it on: a `Continue()` it calls is a trusted continuation.

## A command that invalidates

Every command that changes a cached resource declares its tag:

```csharp
internal sealed record RenameProductCommand(Guid ProductId, string Name) : Command, IInvalidatesCache
{
    public IReadOnlyCollection<string> InvalidatedCacheTags => [GetProductQuery.ProductTag(ProductId)];
}

// Removes by name, so it cannot name the products' ids before it runs.
internal sealed record RemoveProductsCommand(IReadOnlyList<string> Names) : Command<int>, IInvalidatesCache
{
    public IReadOnlyCollection<string> InvalidatedCacheTags => [GetProductQuery.ProductsTag];
}
```

- The tags are read from the command alone, after its checks and before its handler runs. A command that finds what it changes by a criterion declares a tag every entry it may change carries, as `RemoveProducts` declares `products`.
- Feature code never evicts. The dispatcher collects the tags of the outermost command and of every command nested in its Unit of Work, a Domain Event Handler's included, and evicts them once the Unit of Work has committed.

## What the Kit guarantees

- **One `HybridCache.GetOrCreateAsync` per cacheable query.** Concurrent misses of one key in one Host share one handler run, `HybridCache`'s own single flight.
- **Never a failed Result.** A failure is returned to every caller that shared the run and stored nowhere, in memory or in the distributed cache: the next query runs again.
- **After the checks.** The authorization hook and validation run before the cache is read, so a stored answer never skips them.
- **Never inside a write Unit of Work.** A query sent from a command's handler, a nested command's checks or a Domain Event Handler may see uncommitted state, so it neither reads nor stores an entry. A query in the outermost command's own checks runs before the Unit of Work starts and uses the cache.
- **Eviction after the outermost commit only.** A rolled-back Operation evicts nothing, so a valid entry survives a failed command. A command that rolls back after a nested query leaves no entry from it.
- **A failed eviction cannot fail a committed Operation.** When the distributed cache refuses it, the Kit logs `EvictionFailed` at Error level and returns the command's success; this Host's own copy is still evicted.

## Limits

- Single flight is per Host. Two Hosts that miss the same key both run the handler.
- An eviction is certain only in the Host whose Operation committed. Another Host may return its own in-memory copy until it expires; choose `CacheDuration` with that in mind.
- A query that read before a concurrent command committed may store the answer it read, which `HybridCache` keeps until it expires.
- The Kit caches query answers only. An Integration Event Handler or anything else that changes a cached resource without a command declares nothing; such data needs a short duration or no caching.

## Testing

`CachingTests` in the Proving Sample shows the patterns: change a row with plain SQL behind the cache's back, so a stored answer (the old value) differs from a fresh read (the new one); count a handler's runs with a probe; and for Redis, start a `Testcontainers.Redis` container, compose two Hosts with its connection string, and wait until the first Host's entry is in Redis before the second reads it, since `HybridCache` answers before its distributed write completes.
