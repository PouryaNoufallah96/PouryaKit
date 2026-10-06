---
name: web-aspnetcore-facts
description: Ticket #14 facts - minimal-API binding vs filters, Finally convention, .NET 10 exception handler logging, TestServer over HostFixture, dotnet-ef check without writing
metadata:
  type: project
---

Verified in ticket #14 (2026-10-05), ASP.NET Core 10.0.12:

- **Minimal APIs bind service parameters before endpoint filters run**, so a filter cannot begin the scope's Operation in time for an endpoint that injects `OperationContext`. Web wraps the built `RequestDelegate` in a route-group `Finally` convention instead (runs after RequestDelegateFactory sets it). `Finally` is a default interface member: cast `RouteGroupBuilder` to `IEndpointConventionBuilder` to call it.
- **.NET 10 exception handler middleware does not log an exception an `IExceptionHandler` handled**; the handler must log it. Aborted requests (OperationCanceledException + RequestAborted) are answered 499 by the middleware before handlers run.
- With `RequireAuthorization` removed but the begin step kept, an anonymous request answers 500 (no Actor), not 200: a mutation test still fails, but read the status before claiming why.
- `HostFixture.CreateHost` returns `IHost`, which a `WebApplication` is: `builder.WebHost.UseTestServer()` plus `Host.GetTestClient()` (Microsoft.AspNetCore.TestHost) gives an HttpClient with no `PouryaKit.Testing` change. A second `AddAuthentication("Test")` call after the Host's overrides the default scheme.
- A test `AuthenticationHandler` subclass cannot declare a member named `Scheme` (CS0108 under TreatWarningsAsErrors).
- Typed IDs serialize as `{"value":...}` unless the HTTP options carry the Kit converter; since #34 `AddPouryaKitWeb` applies `UsePouryaKitJson` ([[shared-json-facts]]).
- Check `dotnet ef` without writing into a Module's migrations: `dotnet ef dbcontext info --project src/Modules/Catalog/Catalog --startup-project src/ModularMonolith.Host --context CatalogDbContext` from `samples/ModularMonolith`.
- #14 fix round: `WebApplication` inserts routing/authentication/authorization ahead of app middleware, so `UseExceptionHandler()` must come first with the other three called explicitly. Outside Development a bind failure answers an empty 400; `PostConfigure<RouteHandlerOptions>(ThrowOnBadRequest = true)` sends it to the exception handler in every environment. `IProblemDetailsService.TryWriteAsync` declines when `Accept` excludes JSON; `ProblemHttpResult.ExecuteAsync` falls back to writing `application/problem+json`, so write exceptions through it too. The authorization middleware reads endpoints when the Host starts, so an endpoint-build convention that throws fails `StartAsync`.
- A test needing a different composition builds its own Host (`AppFixture.ComposeHost(environment, ...)`) and starts it without migrating; Modules touch the database only on use. A test-only endpoint that must not exist in the shared Host maps itself only when a marker service is registered.
- The worktree guard refused a python heredoc that mutated files in one Bash call with cp; do mutations with the Edit tool and restore with plain `cp` from the scratchpad.
- Sdk.Web globs appsettings*.json as content; an explicit `<Content Include="appsettings.json">` would duplicate it.

**Why:** each decided the Web design or cost a build round.
**How to apply:** when touching PouryaKit.Web, the sample Host or HTTP tests. Related: [[modularity-context-facts]], [[dotnet-test-conventions]].
