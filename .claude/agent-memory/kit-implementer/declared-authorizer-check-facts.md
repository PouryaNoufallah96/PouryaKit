---
name: declared-authorizer-check-facts
description: Ticket #56 facts - why the permission check needs a Cqrs seam, the build-time factory hook, keyed descriptor trap, asserting authorizers through the Gateway
metadata:
  type: project
---

Verified in ticket #56 (2026-10-07):

- **Security code does not run in every Host.** `AddPouryaKitPermissions` runs only where a Composition Root calls it (the failure case is exactly the one that did not), and the Host's `IActorPermissions` is a plain `AddScoped`. The only Kit code every Host runs after all Composition Roots is Modularity, which may not reference Security (plan section C). So the declaration travels on the attribute through a Cqrs contract, `IRequiresAuthorizer`, and the check reads it (in `HostModules` at #56, in Cqrs since #53, below).
- **Build-time hook:** `AddPouryaKitModules` passes `ModularServiceProviderFactory` (wraps `DefaultServiceProviderFactory`) to `ConfigureContainer`; its `CreateServiceProvider` sees the Host's final, still writable collection. A check at the end of `AddPouryaKitModules` would be wrong: the fixture adds Stub in a second call, and a later call may register the authorizer.
- **`ServiceDescriptor.ImplementationType` throws on a keyed descriptor.** Filter `IsKeyedService` first, in Kit code and in test helpers that walk the collection.
- **Resolving `IAuthorizer<T>` from a bare scope throws** ("No Operation Context was set"): `PermissionAuthorizer<T>` injects `OperationContext`. Prove a registered check by sending through the Gateway on a built, unstarted Host (an Actor without the permission gets `PermissionErrors.Denied`).
- Remove only the permission check in a test: descriptors of `IAuthorizer<T>` whose `ImplementationType.Assembly` is Security's; the Stub's own `RefuseSomeStampsAuthorizer` stays and must not satisfy the check (mutation "any authorizer counts" goes red that way).
- Since #53 the check is Cqrs's public `EnsurePouryaKitDeclaredAuthorizers()` (the message names "the Composition Root that registers its handler", not the Module); Modularity's factory calls it, and a Host without Modularity passes it to `ConfigureContainer`, as the monolith template does. The plan's section D Security line ("optional, registered by the application") was left as it was.

**Why:** each decided the #56 design or cost a run.
**How to apply:** when adding another Kit attribute that requires a registration (reuse `IRequiresAuthorizer`), touching the Modularity factory, or testing authorizers. Related: [[security-permission-facts]], [[modularity-context-facts]].
