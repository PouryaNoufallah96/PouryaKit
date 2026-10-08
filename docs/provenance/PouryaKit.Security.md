# PouryaKit.Security provenance

Adopted implementations in `PouryaKit.Security`. Format and rules: [README](README.md).

`IActorPermissions`, `PermissionErrors` and the continuation rule of the permission check (a context the Kit marked `IsTrustedContinuation` is not checked again, ADR 0007 and #57) are new Kit code, so they have no record here.

## Package dependencies

None shipped. `PouryaKit.Security` references `PouryaKit.Cqrs` (since ticket #38) and `PouryaKit.Core` (since ticket #55), as plan section C lists, and no ASP.NET Core framework reference: the authenticated HTTP fallback policy is `PouryaKit.Web`'s (see [its record](PouryaKit.Web.md#authenticated-access-by-default)). Its private MinVer build reference is recorded in [shared private build tools](README.md#shared-private-build-tools).

## Permission check

- **Kit files:** `src/PouryaKit.Security/RequiresPermissionAttribute.cs`, `src/PouryaKit.Security/PermissionAuthorizer.cs`, `src/PouryaKit.Security/SecurityServiceCollectionExtensions.cs`
- **Upstream:** https://github.com/PouryaNoufallah96/cybernod
- **Commit:** bae8ac3b7e46f09e9375647786d03391a500876a
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.Security/Authorization/FeatureAuthorizationHandler.cs`
- **Licence:** MIT
- **Adoption:** pattern
- **Ticket:** #38
- **Intentional differences:**
  - No admin bypass: upstream lets an admin skip every feature check (`FeatureAuthorizationHandler.cs:10-15`); the Kit grants nothing by role, and a named system Actor holds only what the application grants it.
  - The check runs on the dispatcher's authorization hook, for the commands and queries that declare a permission, not as an ASP.NET Core authorization handler and policy, so it applies to an Operation however it arrives (HTTP, a Gateway call, a delivery) and adds no second dispatch path or policy provider.
  - The Actor's permissions come from the application's `IActorPermissions`, not from claims the Kit reads: the Kit reads no ambient request (ADR 0007), and the Proving Sample's Host implements the source from its HTTP caller's claims.
  - A trusted continuation, a context the Kit marked `IsTrustedContinuation`, is not checked again, and a causation alone does not make one; upstream has no continuation.
