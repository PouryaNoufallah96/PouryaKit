---
name: modularity-context-facts
description: Ticket #13 facts - startup validation needs the DI implementation package, Gateway signature change vs parallel tickets, Operation Context seams
metadata:
  type: project
---

Verified in ticket #13 (2026-10-05):

- **Startup validation needs `Microsoft.Extensions.DependencyInjection`.** `ServiceProviderOptions` (ValidateOnBuild, ValidateScopes) and `DefaultServiceProviderFactory` live in the implementation package. `Microsoft.Extensions.Hosting.Abstractions` 10.0.12 (net10.0) depends only on *.Abstractions packages, so Modularity cannot turn validation on within plan section C. `HostApplicationBuilder` enables it only in Development, and no configuration key changes that. #14 (Web) hits the same wall.
- **Changing a public signature that parallel tickets call breaks their merge.** The Gateway gained a required `OperationContext`; C# 14 extension members in `PouryaKit.Testing` (`GatewayTestExtensions`) keep the old `SendAsync(cmd, ct)` form compiling: an instance method that is not applicable falls through to the extension.
- `Microsoft.Extensions.Hosting.Host` collides with the namespace `ModularMonolith.Host` inside `ModularMonolith.Tests`; qualify it fully.
- Host-wide composition state (Modules added, which Module owns each operation) is kept as a singleton `ImplementationInstance` in the service collection, found-or-added, because `AddPouryaKitModules` is called more than once per Host (the test fixture adds the Stub Module in a second call).
- A test can observe the context of another Module's Operation without changing that Module: an `IAuthorizer<ThatQuery>` in the test assembly is picked up by the Stub Module's assembly scan and runs in the other Module's scope.
- Ultrabusiness: the local checkout's HEAD differs from the pinned `a85eacd`, but `git ls-tree`/`git show a85eacd:<path>` against it works for citing files at the pinned commit.

**Why:** each cost a detour or decides how #14-#16 plug in.
**How to apply:** when touching Modularity, Testing or the Gateway API; before adding a reference, check the package's nuspec dependency group. Related: [[persistence-ef-facts]], [[cqrs-design-facts]].
