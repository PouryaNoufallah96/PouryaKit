---
name: modularity-context-facts
description: Ticket #13 facts - startup validation needs the DI implementation package, Gateway signature change vs parallel tickets, Operation Context seams
metadata:
  type: project
---

Verified in ticket #13 (2026-10-05):

- **Startup validation needs `Microsoft.Extensions.DependencyInjection`.** `ServiceProviderOptions` (ValidateOnBuild, ValidateScopes) and `DefaultServiceProviderFactory` live in the implementation package; `Microsoft.Extensions.Hosting.Abstractions` 10.0.12 (net10.0) depends only on *.Abstractions packages, and `HostApplicationBuilder` enables validation only in Development with no configuration key. The owner approved the reference for Modularity on 2026-10-05 (beyond section C); `AddPouryaKitModules` calls `ConfigureContainer(new DefaultServiceProviderFactory(...))`. A validation failure surfaces from `Build()` as an `AggregateException`.
- **The scope's Module lives on Cqrs's `Operation`** (`Operation.Module`, set with the Context by `IServiceProvider.BeginOperation(context, module)`); `ModuleTag` is gone. Delivery adapters (#15/#16) begin their scope the same way without Modularity.
- Asking the main session mid-package with SendMessage worked: the decision came back as a follow-up message after the first report, so deliver everything unblocked and report; the follow-up then resumes the package.
- **Changing a public signature that parallel tickets call breaks their merge.** The Gateway gained a required `OperationContext`; C# 14 extension members in `PouryaKit.Testing` (`GatewayTestExtensions`) keep the old `SendAsync(cmd, ct)` form compiling: an instance method that is not applicable falls through to the extension.
- `Microsoft.Extensions.Hosting.Host` collides with the namespace `ModularMonolith.Host` inside `ModularMonolith.Tests`; qualify it fully.
- Host-wide composition state (Modules added, which Module owns each operation) is kept as a singleton `ImplementationInstance` in the service collection, found-or-added, because `AddPouryaKitModules` is called more than once per Host (the test fixture adds the Stub Module in a second call).
- A test can observe the context of another Module's Operation without changing that Module: an `IAuthorizer<ThatQuery>` in the test assembly is picked up by the Stub Module's assembly scan and runs in the other Module's scope.
- Ultrabusiness: the local checkout's HEAD differs from the pinned `a85eacd`, but `git ls-tree`/`git show a85eacd:<path>` against it works for citing files at the pinned commit.

**Why:** each cost a detour or decides how #14-#16 plug in.
**How to apply:** when touching Modularity, Testing or the Gateway API; before adding a reference, check the package's nuspec dependency group. Related: [[persistence-ef-facts]], [[cqrs-design-facts]].
