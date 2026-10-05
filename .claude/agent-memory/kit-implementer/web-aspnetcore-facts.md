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
- `TypedIdJsonConverterFactory` must be added to `ConfigureHttpJsonOptions` or typed IDs serialize as `{"value":...}`.
- Check `dotnet ef` without writing into a Module's migrations: `dotnet ef dbcontext info --project src/Modules/Catalog/Catalog --startup-project src/ModularMonolith.Host --context CatalogDbContext` from `samples/ModularMonolith`.
- Sdk.Web globs appsettings*.json as content; an explicit `<Content Include="appsettings.json">` would duplicate it.

**Why:** each decided the Web design or cost a build round.
**How to apply:** when touching PouryaKit.Web, the sample Host or HTTP tests. Related: [[modularity-context-facts]], [[dotnet-test-conventions]].
