# PouryaKit.Caching provenance

Adopted implementations in `PouryaKit.Caching`. Format and rules: [README](README.md).

The caching slot is new Kit code, built to the plan's own rule (I.3 Caching, I.6, ADR 0002) and decided there, so it has no record here: its place after the checks in `src/PouryaKit.Cqrs/Pipeline/Dispatcher.cs`, the write Unit of Work bypass, the tags collected on `src/PouryaKit.Cqrs/Operations/Operation.cs` and evicted after the outermost commit, the effective key in `src/PouryaKit.Caching/HybridQueryCache.cs` and the null-tenant sentinel `OperationContext.NoTenant`. So is keeping a failed Result out of the cache, by failing `HybridCache`'s factory, since the reference's get-then-set shape that stores failures is rejected by the plan (below), and keeping out the answer of a read in flight across a commit, through the record of runs in flight that the commit's eviction marks (ticket #93).

## Package dependencies

Checked on nuget.org on 2026-10-07; licence expressions from each package's nuspec. Neither is reciprocal or commercial (ADR 0002).

- `Microsoft.Extensions.Caching.Hybrid` 10.11.0, `MIT`, the latest stable (10.10.0 until ticket #167, 2026-10-24), with a `net10.0` asset. It brings `Microsoft.Extensions.Caching.Abstractions`, `Microsoft.Extensions.Caching.Memory`, `Microsoft.Extensions.Logging.Abstractions` and `Microsoft.Extensions.Options` 10.0.13, all `MIT`.
- No distributed cache. Redis is the application's `IDistributedCache`, through `Microsoft.Extensions.Caching.StackExchangeRedis`, which the Proving Sample's Host references ([ModularMonolith.md](ModularMonolith.md)); `PackageGraphTests` keeps `StackExchange.Redis` and that package out of this package's closure.

Its private MinVer build reference is recorded in [shared private build tools](README.md#shared-private-build-tools).

## Cacheable query and invalidating command contracts

- **Kit files:** `src/PouryaKit.Caching/ICacheableQuery.cs`, `src/PouryaKit.Caching/IInvalidatesCache.cs`, `src/PouryaKit.Caching/HybridQueryCache.cs`
- **Upstream:** https://github.com/PouryaNoufallah96/cybernod
- **Commit:** bae8ac3b7e46f09e9375647786d03391a500876a
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.Core/Messaging/Queries/ICacheableQuery.cs`, `src/BuildingBlocks/BuildingBlocks.Core/Messaging/Commands/IInvalidateCacheRequest.cs`, `src/BuildingBlocks/BuildingBlocks.Caching/Services/HybridCachingService.cs`
- **Licence:** MIT
- **Adoption:** pattern
- **Ticket:** #43
- **Intentional differences:**
  - The query declares a key, a duration and tags, as upstream's `CacheKey`, `CacheDuration` and `Tags`. The flags `IsCacheEnabled`, `AppendCustomRequestHeaders` and `AppendIdentityHeader` are dropped: implementing the interface opts in, and the Kit always adds the Actor, by kind and id, the tenant and the query's type to the key, from the Operation Context rather than from HTTP headers, so no key can leave the Actor out.
  - A command declares only tags, `InvalidatedCacheTags`; upstream's `CacheKeys` and header flags are dropped, since a key is per Actor and a tag reaches every Actor's entry.
  - One `HybridCache.GetOrCreateAsync` per query, where upstream reads with `GetOrCreateAsync` and writes with `SetAsync` in two calls. The get-then-set behaviour (`src/BuildingBlocks/BuildingBlocks.Caching/Middlewares/CachingBehavior.cs:50-84`) stores whatever the handler returned, failures included, and loses the single flight; the plan rejects it (I.3 Caching).
  - Eviction runs once the outermost command's Unit of Work has committed, never on rollback and never inside a write Unit of Work, where upstream's `InvalidateCachingBehavior` evicts after the handler, before any commit.
  - An entry is the success value as JSON in the Kit's representation, read back on every hit; no MessagePack serializer and no FusionCache beside `HybridCache` (plan I.3, refused list).
