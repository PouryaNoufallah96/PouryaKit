---
name: architecture-test-facts
description: Ticket #19 facts - BCL IL reader for structure rules, how the sample's EF models and Modules are found, routing's empty 405/415, Solo-node recovery race between test Hosts
metadata:
  type: project
---

Verified in ticket #19 (2026-10-06); check `tests/PouryaKit.ArchitectureTests` before relying on the design:

- **IL scanning needs no package.** `MethodBody.GetILAsByteArray()` plus an opcode table built from `OpCodes` fields, tokens resolved with `Module.ResolveMethod(token, typeArgs, methodArgs)` / `ResolveString`. Async methods and lambdas live in compiler-generated nested types (`<Handle>d__0`, `<>c`): scan `assembly.GetTypes()` (nested included) and attribute to the outermost declaring type. A mutation that skipped `<`-named types failed both the fixture and the positive anti-vacuous checks.
- **Transport check** reads `MetadataReader.TypeReferences` (System.Reflection.Metadata, BCL): names every Wolverine/JasperFx type an assembly's code or signatures touch. The compiler drops an unused assembly reference, so a fixture must really use the forbidden type.
- **Sample Module discovery:** `DependencyContext.Default.RuntimeLibraries` with `Type == "project"` gives the repo's own assemblies; walk the Host's references through them only (walking NuGet assemblies can fail to load).
- **EF models with real table names:** build `ModularMonolithHost.CreateBuilder([... "--ConnectionStrings:app=Host=localhost;Database=x"])` + `ModularMonolithHost.Build`, never start; resolve each internal DbContext by `Type` from a scope; `GetService<IDesignTimeModel>().Model` (namespace `Microsoft.EntityFrameworkCore.Metadata`). A hand-built options object would miss snake_case and the schema.
- **`project.assets.json`:** `libraries` of type `project` is transitive; direct project refs are `project.restore.frameworks.<tfm>.projectReferences`, direct packages `project.frameworks.<tfm>.dependencies`. Nuspec path = `packageFolders[0]` + library `path` + the `.nuspec` in `files`.
- **Routing answers 405 and 415 itself** with an empty body ("415 HTTP Unsupported Media Type" endpoint), before any endpoint or exception handler: `ThrowOnBadRequest` does not reach it. Web now registers status code pages through an `IStartupFilter`.
- **Recovery race between test Hosts:** the fixture Host and any Host a test composes all run Solo as node 1 on one store, so a dead Host's delivery can be recovered by two live Hosts at once (seen once in ten runs: 3 attempts instead of 2; the Receipt kept one effect). Count attempts as a range, or assert effects, when a test kills a Host while the fixture Host runs.
- **Distinct node numbers cannot fix the Solo double recovery** (#19 fix round, decompiled 6.46.0): `NodeAgentController.StartSoloModeAsync` hard-codes `AssignedNodeNumber = 1` and first calls `ReleaseAllOwnershipAsync()` (a starting Solo Host releases every live Host's rows); `DurabilitySettings.AssignedNodeNumber` has an internal setter; `RecoverIncomingMessagesCommand` loads owner-0 rows with a plain select and `MessageDatabase.ReassignIncomingAsync` updates by id with no `owner_id = 0` guard, so two live pollers both take a row whatever their numbers.
- F4: a ProjectReference no emitted code uses leaves no AssemblyRef; read the restored closure (`RestoredPackage.Of(csproj)`, project paths from the Host's `project.assets.json` `libraries[*].path`).
- EF Core 10 declares `ExecuteUpdate(Async)`/`ExecuteDelete(Async)` on `EntityFrameworkQueryableExtensions` (Microsoft.EntityFrameworkCore namespace).
- Test-only failure injection without touching a Module: `DeliveryProbes.FailAfterReceipt(subscription)` throws in the `IReceipts` decorator after the Receipt is recorded. The Stub Module registers its validators (`AddPouryaKitValidation`) since #19.
- Suite time (230 tests, ~30 s): one PostgreSQL container per run (not per test), ~5 s fixed start, ~17 s in 25 tests whose waits are the mechanism (retry delays and polling in `PouryaKit.Testing`, stale times, second Hosts). Tests run sequentially (`CollectionPerAssembly`).
- The guard refused a `for ... sed -E "s/.../$f/"` loop over files; one `grep ... | cut | sed` pipeline on a glob passed.

**Why:** each decided a #19 design or cost a run.
**How to apply:** when adding a structure rule, a Kit package (update `PackageGraph`'s table), or a test that kills a Host. Related: [[dead-letter-and-stale-time-facts]], [[web-aspnetcore-facts]], [[modularity-context-facts]].
