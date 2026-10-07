---
name: web-defaults-facts
description: Ticket #36 facts - RequireCors answers no preflight on minimal APIs, compression cannot be made inert, ProblemDetailsDefaults has no 429, rate limiter seams, mutation script shape
metadata:
  type: project
---

Verified in ticket #36 (2026-10-07), ASP.NET Core 10.0.12, by decompiling the shared framework with `ilspycmd -t <Type> <dll>` (write each to the scratchpad; one plain command per call, a variable in the path is refused):

- **`RequireCors(name)` on a minimal API endpoint answers no preflight.** `RouteEndpointDataSource` adds `HttpMethodMetadata(methods)` with `acceptCorsPreflight: false`, so an `OPTIONS` preflight routes to the 405 endpoint, which has no CORS metadata; with a fallback policy it then answers 401. Only a pipeline policy (`app.UseCors(name)` or a default policy) answers preflights. The Kit's sample uses `UseCors(BrowserPolicy)`.
- **Response compression cannot be present and inert.** `ResponseCompressionProvider` falls back to Brotli + gzip when `Providers` is empty and to `ResponseCompressionDefaults.MimeTypes` (JSON yes, `application/problem+json` no) when `MimeTypes` is empty. So the Kit adds `UseResponseCompression()` from an `IStartupFilter` only when enabled. Both providers already default to `CompressionLevel.Fastest`.
- **`ProblemDetailsDefaults` has no 429 entry:** a 429 problem response has `title` "Too Many Requests" and `status`, but no `type`.
- A rate limiter rejection with `RejectionStatusCode` set and no `OnRejected` leaves the body empty, so `ProblemStatusCodePages` writes the problem response: no second writer. `UseRateLimiter()` with no `GlobalLimiter` and no endpoint policy passes through; `UseCors(name)` with an unknown name passes through.
- `CorsService` reads `IOptions<CorsOptions>.Value` when the pipeline is built, so a `CorsOptions` configure that depends on validated Kit options fails `StartAsync` even without `ValidateOnStart` (the mutation stayed green; mutate the validator instead).
- `IStartupValidator` (ValidateOnStart) runs before hosted services, so a misconfigured test Host throws `OptionsValidationException` from `StartAsync` with nothing of Wolverine started.
- A test Host gets its own configuration through `AppFixture.ComposeHost(..., builder => builder.Configuration.AddInMemoryCollection(...))`: options bound with `Configure<T>(section)` read it when built. TestServer with an `https://` base address makes `Request.IsHttps` true, and the test client does not decompress.
- **Review F1: "partition by the Actor" means the Actor the Operation runs as**, not the raw claim: an `AllowAnonymousAs` endpoint's named Actor for anonymous callers, the continued Actor for a trusted peer. One internal `HttpCallerContext.Resolve` now feeds both the endpoint's `BeginOperation` and the partitioner (the limiter reads the endpoint's metadata through `GetEndpoint()`, since it runs after routing); it returns null instead of throwing for an unresolved caller, and the partitioner catches only the `BadHttpRequestException` of a peer's unreadable headers. Any later "by the Actor" feature (caching keys, idempotency) should reuse it.
- Mutation runs: a python script in the scratchpad, run with the worktree as argument, copies the file, replaces one string, runs `dotnet test --filter-class` with `cwd=` the worktree, prints the red tests, and copies back in `finally`. A combined `python3 - <<EOF` plus `dotnet test` in one Bash call was refused; write the script with Write first.

**Why:** each decided the #36 design or cost a round.
**How to apply:** for #53 (templates compose the same pipeline), any CORS, compression or limiter change, and any test that asserts a problem response's `type`. Related: [[web-aspnetcore-facts]], [[security-permission-facts]], [[worktree-and-check-gotchas]].
