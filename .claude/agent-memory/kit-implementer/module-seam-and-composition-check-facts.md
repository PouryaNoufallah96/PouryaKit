---
name: module-seam-and-composition-check-facts
description: Ticket #61 facts - Host.StartAsync order vs StartingAsync checks, JasperFx start failure through WebApplicationFactory, zero-argument registrations scanning a test assembly, null-owned test declarations, test-only handler assembly
metadata:
  type: project
---

Verified in ticket #61 (2026-10-07), .NET 10.0.12, WolverineFx 6.46.0, JasperFx 2.80.2; check the merged code first:

- **`Host.StartAsync` order:** it resolves every `IHostedService` first (constructing `WolverineRuntime`, so `WolverineOptions` and every Kit `IWolverineExtension` check run then), then `IStartupValidator` (`ValidateOnStart` options), then each `IHostedLifecycleService.StartingAsync`, then `StartAsync`. So a Wolverine extension's refusal (missing publication, wire-name clash) and an options failure surface before any composition check, and a composition check still runs before Wolverine or the web server starts. One `StartingAsync` exception is rethrown unwrapped (assert `InvalidOperationException`, not `AggregateException`); nothing needs stopping after it.
- **JasperFx `RunJasperFxCommands` with `AutoStartHost`** starts the pre-built Host synchronously in `buildExecutor`, with no catch, so a start failure leaves `Main` as is and `WebApplicationFactory<Program>.Services` rethrows the same `InvalidOperationException` (the generated `HostTests` passed under `validate.py`).
- **Decision A trap:** `module.AddPouryaKitCqrs()` with no assembly on a test Module defined in `ModularMonolith.Tests` scans that whole assembly (every Stub handler), and `EnsureUnitOfWorkForCommands` then throws. Test-only Modules register their one handler by hand; `AddPouryaKitModules` registers the dispatcher.
- **Null-owned declarations fail at start in a Host of Modules.** A test that adds `builder.Services.Publish/Subscribe/...` to an `AppFixture.ComposeHost` Host and starts it must name the Module (`typeof(StubModule)`, `typeof(CatalogModule)`); a test that only asserts an earlier failure (Wolverine extension at hosted-service resolution) still gets its own message.
- **A valid Notification Handler no Module declares** needs an assembly nothing scans: `tests/ModularMonolith.Tests/Unowned` (its own csproj, excluded from the test project's sources, in the slnx). Every handler in the Stub's assembly belongs to the Stub, so declaring them again without a Module is refused at registration, not at start.
- **Recording a Module needs a Kit registration on its builder** (`IModuleServices.ModuleOf`/`FeatureAssembliesOf`); a Module whose Composition Root makes none (test `QueryOnlyModule`) is not in `HostComposition.Modules`.
- Mutation script `mutate61.py` in the session scratchpad: 8 mutations, each restored from a backup in `finally`. `x ?? (DateTime.Now.Year > 0 ? new T() : null) ?? throw ...` disables a `?? throw` guard without a constant-condition warning.
- Edit traps: `s.replace("_module", "Module")` also rewrote `_modules` (use `\b`); IDE0032 is an error for a private field read by an expression-bodied property (make it an auto property). A `cat > <scratchpad>/x.py <<'EOF'` was refused once mid-session after several passed: write scripts with the Write tool.

**Why:** each cost a run or decided a #61 test, and #62 adds its checks on the same runner.
**How to apply:** when adding a composition check, a test Host with Host-level declarations, or a test-only Module. Related: [[declared-authorizer-check-facts]], [[codegen-production-facts]], [[messaging-delivery-facts]].
