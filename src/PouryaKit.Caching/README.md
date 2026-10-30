![PouryaKit.Caching](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-public/assets-v1/assets/covers/PouryaKit.Caching.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Caching?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Caching)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Caching

Query successes cached over `HybridCache`. A query opts in with `ICacheableQuery` and names its key, its duration and the cache tags of what its answer reads. The Kit adds the query's type, the tenant, the Actor and the culture to the key, so an entry answers only the query and the Actor that stored it. A command declares the tags it invalidates with `IInvalidatesCache`, and the Kit evicts them once its Operation commits.

## Install

```bash
dotnet add package PouryaKit.Caching --prerelease
```

## When to use it

- A query is read far more often than what it reads changes.
- A command changes something a cached query answers, and the next read must not see the old answer.
- Several Hosts of the application should share cached answers through Redis.

It fills the query cache of [`PouryaKit.Cqrs`](https://www.nuget.org/packages/PouryaKit.Cqrs). A Host that registers a distributed cache such as Redis shares entries between its Hosts; the Kit references none itself.

## Quick start

Register it in the Host:

```csharp
builder.Services.AddPouryaKitCaching();
```

Declare the key, duration and tags on the query, and what a command invalidates, as the Proving Sample does:

```csharp
internal sealed record GetProductQuery(Guid ProductId) : Query<ProductDto>, ICacheableQuery
{
    public static string ProductTag(Guid productId) => $"product:{productId}";

    public const string ProductsTag = "products";

    public string CacheKey => ProductId.ToString();

    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);

    public IReadOnlyCollection<string> CacheTags => [ProductTag(ProductId), ProductsTag];
}

internal sealed record RenameProductCommand(Guid ProductId, string Name) : Command, IInvalidatesCache
{
    public IReadOnlyCollection<string> InvalidatedCacheTags => [GetProductQuery.ProductTag(ProductId)];
}
```

## What you get

- Only successes cached, keyed per Actor, tenant and culture.
- An eviction once the command's Operation commits; a rolled-back Operation evicts nothing.
- Concurrent misses of one key in one Host share one run of the handler.
- A Host that fails when it is built if it has a cacheable query and no cache registered.

> [!WARNING]
> An eviction is certain only in the Host that committed. Another Host may answer from its own in-memory copy until the entry expires. Only a command evicts: data changed without one needs a short duration or no caching.

## Learn more

- [Caching](https://pouryakit.vercel.app/docs/capabilities/caching)
- [A cacheable query](https://pouryakit.vercel.app/docs/capabilities/caching#a-cacheable-query)
- [A command that invalidates](https://pouryakit.vercel.app/docs/capabilities/caching#a-command-that-invalidates)
- [What the Kit guarantees](https://pouryakit.vercel.app/docs/capabilities/caching#what-the-kit-guarantees)
