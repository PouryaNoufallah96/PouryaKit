---
name: misconfigured-host-facts
description: Ticket #62 facts - Wolverine queue modes final only after start, IAuthenticationFeature as the authentication-ran signal, handler builder filter sees the completed chain, test bin appsettings sets ConnectionStrings app
metadata:
  type: project
---

Verified in ticket #62 (2026-10-08), ASP.NET Core 10.0.12, Microsoft.Extensions.Http 10.0.12 and .Http.Resilience 10.10.0, WolverineFx 6.46.0; check the merged code first:

- **Wolverine endpoint modes are final only after Wolverine starts.** `Endpoint.Compile` (run in the runtime's start) applies `Transports.EndpointPolicies` (`UseDurableLocalQueues`) then the endpoint's delayed configuration (`LocalQueue(x).BufferedInMemory()`). A composition check at `StartingAsync` reads every queue's default, so the queue/broker durability check stays in `StartupCheck.StartedAsync`; only its Outbox part became `OutboxCheck` on the runner.
- **Authentication-ran signal:** `AuthenticationMiddleware.Invoke` sets `IAuthenticationFeature` first, for every request, anonymous too. `WebApplicationBuilder.ConfigureApplication` adds `UseRouting` (when data sources exist and the app did not call it), `UseAuthentication` and `UseAuthorization` (when their services exist and the app did not call them) ahead of the app's pipeline, so a limiter-order trap needs an explicit `UseAuthentication()` after `UseRateLimiter()`. Routing sets nothing on a no-match, so a limiter before routing is undetectable.
- **Completed HttpClient chain:** `DefaultHttpClientFactory.CreateHandlerEntry` wraps `HttpMessageHandlerBuilderActions` (where `RemoveAllResilienceHandlers` acts, through `ConfigureAdditionalHttpMessageHandlers`) in every `IHttpMessageHandlerBuilderFilter`; a filter that calls `next(builder)` then reads `AdditionalHandlers` sees the final chain. The entry is a `Lazy`, so a throw there repeats on every `CreateClient` of that name. `ResilienceHandler` is public.
- **The test bin has the sample Host's appsettings.json** (`ConnectionStrings:app` = localhost): a plain `Host.CreateApplicationBuilder` in a test sees it, so a "missing connection string" test must use another name.
- A test Host without Modules that now needs the runner (`AddPouryaKitCompositionChecks`) also gets `ValidateOnBuild`: registering the whole Stub assembly's Notification Handlers then fails at `Build` on their dependencies; use the `Unowned` assembly. `UnitOfWorkCheck` also flags the Kit's own `CommandHandler<Deliver>` in a Wolverine Host with no Unit of Work (a second line, after the first problem).
- Group's `Finally` refusals (AllowAnonymous, RequireIdempotencyKey on GET) surface at `host.StartAsync` in the AppFixture Host.
- `git add -p` is unavailable: when two items share a file, plan the commit split before editing, or commit the items together and say so.

**Why:** each decided a #62 design point or cost a run.
**How to apply:** for any later start-time check, HTTP pipeline order check, outgoing-client rule or Wolverine endpoint inspection. Related: [[module-seam-and-composition-check-facts]], [[outgoing-http-facts]], [[web-defaults-facts]], [[messaging-delivery-facts]].
