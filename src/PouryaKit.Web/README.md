![PouryaKit.Web](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-public/assets-v1/assets/covers/PouryaKit.Web.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Web?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Web)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Web

The Kit's HTTP boundary. Each Feature has its own endpoint, a small `IEndpoint` class beside its command or query, which a Module's Composition Root maps under its route prefix. The Kit authenticates each request and begins its Operation with an Operation Context built from the verified claims, and the endpoint turns the Result into a response with `ToHttpResult()`.

## Install

```bash
dotnet add package PouryaKit.Web --prerelease
```

## When to use it

- You are writing an endpoint, or composing the Host's HTTP pipeline.
- You choose a response's shape, or bind page, sort and filter parameters from the query string.
- An endpoint must be public, or a repeatable write must require an idempotency key.
- You read the verified claims into the Operation Context, or call another Host with it.
- You configure CORS policies, response compression or the rate limit per Actor.

It pairs with [`PouryaKit.Security.JwtBearer`](https://www.nuget.org/packages/PouryaKit.Security.JwtBearer) for bearer tokens, [`PouryaKit.Web.OpenApi`](https://www.nuget.org/packages/PouryaKit.Web.OpenApi) for versions and documents, and [`PouryaKit.Modularity`](https://www.nuget.org/packages/PouryaKit.Modularity) for the Gateway its endpoints call.

## Quick start

Compose the Host's pipeline:

```csharp
builder.Services.AddPouryaKitWeb();
app.UseExceptionHandler();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapPouryaKitModuleEndpoints();
```

Find the Module's endpoints in its Composition Root:

```csharp
module.AddPouryaKitEndpoints("/inventory");
```

Map one endpoint per Feature, as the Proving Sample does:

```csharp
internal sealed class GetStockItemEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapGet(
            "/stock-items/{productId}",
            async (Guid productId, OperationContext context, IGatewayProcessor<InventoryModule> inventory, CancellationToken cancellationToken) =>
                (await inventory.QueryAsync(new GetStockItemQuery(productId), context, cancellationToken)).ToHttpResult()
        );
}
```

## What you get

- Authenticated access by default: a public endpoint says so, and names the system Actor it runs as.
- One problem response for every failure, chosen by the Errors' kinds.
- An `OperationContext` built from the verified claims.
- Outgoing HTTP that carries the Operation Context, with the standard retry for safe methods.
- A Page bound from the query string, and an idempotency key required on a repeatable write.

> [!WARNING]
> Every endpoint is authenticated unless it says otherwise, and a tenant-scoped Module maps no public endpoint, since an anonymous request has no tenant.

## Learn more

- [Web](https://pouryakit.vercel.app/docs/capabilities/web)
- [Add an endpoint](https://pouryakit.vercel.app/docs/guides/add-an-endpoint)
- [Problem responses](https://pouryakit.vercel.app/docs/capabilities/web#problem-responses)
- [The caller and the Actor](https://pouryakit.vercel.app/docs/capabilities/web#the-caller-and-the-actor)
- [Error codes](https://pouryakit.vercel.app/docs/reference/error-codes)
