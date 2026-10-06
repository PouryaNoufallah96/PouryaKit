---
name: outgoing-http-facts
description: Ticket #48 facts - HttpClientFactory handler scope, standard resilience handler API and unsafe-method rule, WireMock.Net 2.18 traps, testing a reset connection
metadata:
  type: project
---

Verified in ticket #48 (2026-10-07), Microsoft.Extensions.Http.Resilience 10.10.0, WireMock.Net 2.18.0:

- **HttpClientFactory builds `DelegatingHandler`s in its own handler scope**, never the calling Operation's, so a handler cannot inject the scoped `OperationContext`; reading `IHttpContextAccessor` is the ambient read ADR 0007 rejects. The Kit's handler reads the context from `request.Options` (`WithOperationContext(context)`); a typed client (transient, resolved in the caller's scope) can inject `OperationContext` itself.
- `AddStandardResilienceHandler()` returns `IHttpStandardResiliencePipelineBuilder`, which does not chain back to `IHttpClientBuilder`; its `.Configure(Action)` needs `using Microsoft.Extensions.Http.Resilience;`. Its options are named by `pipeline.PipelineName`, so `services.PostConfigure<HttpStandardResilienceOptions>(pipeline.PipelineName, o => o.Retry.DisableForUnsafeHttpMethods())` runs after every application `Configure`.
- The default standard retry retries POST. `DisableForUnsafeHttpMethods()` also stops a retry after a reset connection (an exception outcome, no response), proved with a raw `TcpListener` that resets each socket (`LingerState = new(true, 0)`): POST 1 connection, GET 4.
- WireMock.Net 2.18 `WithFault(FaultType.EMPTY_RESPONSE / MALFORMED_RESPONSE_CHUNK)` answered 200 in a probe: it cannot stand in for a broken connection. Scenarios: a mapping with no `WhenStateIs` matches only the start state; `ResetScenario(name)` and `ResetLogEntries()` exist on `IWireMockServer`. The full `WireMock.Net` package brings Roslyn 4.8 (ProtoBuf part) and GraphQL; it restored and ran beside `WolverineFx.RuntimeCompilation` with no conflict.
- The worktree guard refused a `python3 - <<'EOF'` edit of `AppFixture.cs` (cause unclear; the same shape passed for other files); the Edit tool worked.
- Files the #48 brief left out that its change made stale: `.agents/skills/pourya-kit/references/security.md` ("An HTTP request always starts a new chain"), the Web row of `SKILL.md`, and `scripts/packaging/` consumers (no Web HTTP-client use).

**Why:** each decided the #48 design or would cost a round on #52/#53.
**How to apply:** for #52 (ServiceDefaults recipes, one resilience pipeline), #53, and any outgoing HTTP or trusted-peer work. Related: [[web-aspnetcore-facts]], [[security-permission-facts]], [[worktree-and-check-gotchas]].
