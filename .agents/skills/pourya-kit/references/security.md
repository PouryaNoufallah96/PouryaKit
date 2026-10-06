# Security

`PouryaKit.Security`: a permission declared on a command or query, checked through the pipeline's existing authorization hook against the application's source of an Actor's permissions, and never checked again for a trusted continuation. Namespace `PouryaKit.Security`. It references `PouryaKit.Cqrs` only, no ASP.NET Core, so a Module or a Host without HTTP uses it too. Authenticated HTTP access by default is `PouryaKit.Web`'s ([web.md](web.md#the-caller-and-the-actor)).

- [Declaring a permission](#declaring-a-permission)
- [The Actor's permissions](#the-actors-permissions)
- [Denial](#denial)
- [Continuations](#continuations)
- [Testing permissions](#testing-permissions)

The Kit has no role system, policy framework, identity store or admin bypass, and issues no token. A rule that is not "this Actor holds this permission", such as one that reads the command's data, is an `IAuthorizer<T>` of the Module's own ([commands-queries-validation.md](commands-queries-validation.md#authorization-hook)).

## Declaring a permission

The Proving Sample's `RemoveProducts` command, in `Products/Features/RemoveProducts/`:

```csharp
using PouryaKit.Cqrs;
using PouryaKit.Security;

/// <summary>Removes every product whose name, as stored, is one of these; returns how many it removed.</summary>
[RequiresPermission(RemoveProductsCommand.Permission)]
internal sealed record RemoveProductsCommand(IReadOnlyList<string> Names) : Command<int>
{
    public const string Permission = "catalog.products.remove";
}

// In the Composition Root, beside AddPouryaKitCqrs and AddPouryaKitValidation:
// module.AddPouryaKitPermissions(typeof(CatalogModule).Assembly);
```

- Put `[RequiresPermission(name)]` on the command or query record, one permission each. Name it `<module>.<resource>.<action>` and keep the name in a constant on the record, so tests and grants name it without repeating the string.
- `module.AddPouryaKitPermissions(assembly)` in the Module's Composition Root registers the check for every command and query of the assembly that declares one, internal ones included, and for nothing else. Call it in every Composition Root whose Features declare one.
- A declared permission whose check no Composition Root registered fails the Host when it is built, so it never runs unchecked. `Build()` throws `InvalidOperationException`, one line per command or query:

  ```text
  Catalog.Products.Features.RemoveProducts.RemoveProductsCommand declares RequiresPermissionAttribute, but its check is not registered, so it would run unchecked. Call AddPouryaKitPermissions with the assembly Catalog in the Composition Root of CatalogModule.
  ```

  Fix it by adding `module.AddPouryaKitPermissions(typeof(CatalogModule).Assembly)` to that Composition Root. Any Composition Root may register it, so a check registered from another Module passes, and a Host whose Modules declare no permission needs no Security registration at all. Only the permission check counts: an `IAuthorizer<T>` of the Module's own for the same command does not stand in for it. `AddPouryaKitModules` runs this check; a Host that replaces its service provider factory with `ConfigureContainer`, or an application without Modularity, loses it.
- The attribute on a type that is not one concrete command or query fails the Host while it is composed: the dispatcher would never check it.
- The check runs in the pipeline's authorization step, before validation and the Unit of Work, with any other authorizer of the same command or query.

## The Actor's permissions

The application says which permissions an Actor holds by implementing `IActorPermissions` and registering it; the Kit reads no claim and no request itself. The Proving Sample's Host reads them from the HTTP caller's claims:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using PouryaKit.Cqrs;
using PouryaKit.Security;
using PouryaKit.Web;

// An HTTP caller holds the "permission" claims its authentication scheme issued; any other Actor, a named system Actor
// included, holds none.
public sealed class CallerPermissions(IHttpContextAccessor http, IOptions<PouryaKitWebOptions> web) : IActorPermissions
{
    public const string ClaimType = "permission";

    public Task<bool> HasPermissionAsync(OperationContext context, string permission, CancellationToken cancellationToken)
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

// In the Host:
// builder.Services.AddHttpContextAccessor();
// builder.Services.AddScoped<IActorPermissions, CallerPermissions>();
```

- `HasPermissionAsync(context, permission, ct)` receives the Operation's `OperationContext`, so the answer can depend on the Actor and the tenant. It is asked only for an Operation that is not a continuation.
- Match the claims to the context's Actor, as `CallerPermissions` does: code inside a request may run an Operation as another Actor, which the caller's claims say nothing about.
- Independent system work, such as a hosted job running as `Actor.System("catalog.reconciler")`, holds only the permissions the application's source grants that named Actor. Nothing holds a permission by default.
- A Host whose Modules declare permissions and that registers no `IActorPermissions` fails when it is built: `AddPouryaKitModules` validates the container.

## Denial

- An Actor without the permission gets `PermissionErrors.Denied` (`Permission.Denied`, kind `Forbidden`); `ToHttpResult()` answers it with 403 and the Error in the problem response's `errors` ([web.md](web.md#problem-responses)).
- A denied command commits nothing. Denied as a nested command, it marks the Operation rollback-only like any failed nested command, so the outer command commits nothing either.

## Continuations

- An Operation whose `OperationContext` has a `CausationId` is a continuation of the authorized Operation that caused it (ADR 0007): a Notification Handler's or Integration Event Handler's delivery, a synchronous question through another Module's Contracts, or a Gateway call made with `context.Continue()`. The permission check lets it run without asking `IActorPermissions` again, so durable work never depends on what its Actor may do by the time it runs (G.6).
- Only the permission check stands aside. Every other `IAuthorizer<T>` registered for the continued command or query still runs, with the original Actor, and can refuse it; recognise a continuation there by `context.CausationId`.
- An HTTP request always starts a new chain of work, with no causation, so a caller can never present its request as a continuation.
- Never create a context with a causation to get past a check: a continuation is work an authorized Operation caused, passed on with `Continue()` or restored by a delivery.

## Testing permissions

- Make the test authentication scheme able to issue permission claims. The Proving Sample's `TestAuthentication` turns a `Test-Permissions` header, comma-separated, into `permission` claims, and `AppFixture.Client("ada", permissions: RemoveProductsCommand.Permission)` sets it.
- Gateway tests run as `GatewayTestExtensions.TestActor`, independent work with no HTTP caller. Grant it, in the test Host only, the permissions those tests need, by name, as a named system Actor of the Host would be granted them; delegate every other Actor to the Host's own source. The Proving Sample's `TestActorPermissions` does this and is registered after the Host's composition. Never grant every permission or every Actor.
- Prove a declared permission over HTTP both ways, the effect committed for a caller holding it and a 403 with nothing committed for one without it, and read the rows back from the database.
- Prove a continuation through a real delivery: an Actor who holds no permission raises a Domain Event whose Notification Handler sends the protected command, and the effect commits; the same Actor sending the command directly is refused.
