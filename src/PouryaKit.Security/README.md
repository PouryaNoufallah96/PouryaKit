![PouryaKit.Security](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-pub/assets-v1/assets/covers/PouryaKit.Security.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Security?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Security)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Security

Permissions on commands and queries, and PBKDF2 password hashing. You put `[RequiresPermission]` on a command or query, and the pipeline's authorization step asks your application's `IActorPermissions` before validation and the handler run. An Actor without the permission gets an Error and nothing commits. The Kit issues no token and has no role system, policy framework or identity store.

## Install

```bash
dotnet add package PouryaKit.Security --prerelease
```

## When to use it

- A command or query may run only for Actors that hold a permission.
- You supply the permissions an Actor holds, from claims or your own store.
- You decide what a continuation or a system Actor may run.
- Your application stores its own passwords and needs to hash, verify and upgrade them.

It plugs into the pipeline of [`PouryaKit.Cqrs`](https://www.nuget.org/packages/PouryaKit.Cqrs). [`PouryaKit.Security.JwtBearer`](https://www.nuget.org/packages/PouryaKit.Security.JwtBearer) turns bearer tokens into the Actor and, where the token carries one, the tenant.

## Quick start

Declare the permission on the record, as the Proving Sample does:

```csharp
[RequiresPermission(RemoveProductsCommand.Permission)]
internal sealed record RemoveProductsCommand(IReadOnlyList<string> Names)
    : Command<int>,
        IInvalidatesCache
{
    public const string Permission = "catalog.products.remove";

    public IReadOnlyCollection<string> InvalidatedCacheTags => [GetProductQuery.ProductsTag];
}
```

Register the check in the Composition Root and your source of permissions in the Host:

```csharp
module.AddPouryaKitPermissions();
builder.Services.AddScoped<IActorPermissions, CallerPermissions>();
```

Say which permissions an Actor holds:

```csharp
public sealed class CallerPermissions(IHttpContextAccessor http, IOptions<PouryaKitWebOptions> web)
    : IActorPermissions
{
    public const string ClaimType = "permission";

    public Task<bool> HasPermissionAsync(
        OperationContext context,
        string permission,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        var caller = http.HttpContext?.User;
        return Task.FromResult(
            caller?.Identity?.IsAuthenticated == true
                && context.Actor.Kind == ActorKind.Person
                && caller.FindFirstValue(web.Value.ActorClaim) == context.Actor.Id
                && caller.HasClaim(ClaimType, permission)
        );
    }
}
```

## What you get

- One declared rule, checked in one place in the pipeline, before validation and the handler.
- A denial that is an Error, with nothing committed.
- A trusted continuation that is not asked again, such as a delivery the Kit restored.
- Password hashes that say how they were made, so a hasher can upgrade them.

> [!WARNING]
> Nothing holds a permission by default. The Kit reads no claim itself: your `IActorPermissions` decides for every Actor, a named system Actor included. Match the claims to the context's Actor, since code inside a request may run an Operation as someone else.

## Learn more

- [Security](https://pouryakit.vercel.app/docs/capabilities/security)
- [Secure an Operation](https://pouryakit.vercel.app/docs/guides/secure-an-operation)
- [Continuations](https://pouryakit.vercel.app/docs/capabilities/security#continuations)
- [Password hashing](https://pouryakit.vercel.app/docs/capabilities/security#password-hashing)
