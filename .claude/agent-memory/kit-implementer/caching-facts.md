---
name: caching-facts
description: Ticket #43 facts - HybridCache 10.10 queues a cancellable run without the caller's context (pass no token), reading check before storing, factory throw stores nothing, StampedeJoin proof, Redis write lag, mutation traps
metadata:
  type: project
---

Verified in ticket #43 (2026-10-07), Microsoft.Extensions.Caching.Hybrid 10.10.0 (decompiled with ilspycmd), StackExchangeRedis 10.0.12, redis:8.10.1. Check the merged code first:

- **HybridCache drops the caller's ExecutionContext** whenever the token can be cancelled (every HTTP and test token): `GetOrCreateAsync` then calls `ThreadPool.UnsafeQueueUserWorkItem`, and only a non-cancellable token takes `ExecuteDirectAsync`, inline on the starting call. Restoring the ExecutionContext in the factory was not enough (review F1, high): a starting caller whose token is cancelled leaves, its Gateway scope is disposed and `AuthorizedOperation` ends, while the shared token stays live for its joiners, so the run goes on with a disposed DbContext. Fix: pass `CancellationToken.None`; the starting call then runs and waits for the handler, joiners share it, and the handler gets `CancellationToken.None`. Restore became unneeded and was removed.
- **The reading boundary's tracked-state check runs at its Dispose, after `Steps` returns** (review F2, high), i.e. after HybridCache stored the answer. Close it early in the cached delegate, on a success, before the factory returns; `ReadingBoundary.Dispose` is idempotent, so the pipeline's later close does nothing. The EF guard exposes only Dispose, and EFCore was outside allowed_files.
- **A factory exception stores nothing and logs nothing** (only an EventSource counter); every joined caller gets the same exception. That is how a failed Result stays out (private `NotStored` carrying the Errors).
- **Keys with control characters or over 1024 chars silently run uncached** (`ValidateKey`). The effective key escapes each part with `Uri.EscapeDataString`, joined by `|`.
- **Default JSON** reads a keyed `JsonSerializerOptions` under `typeof(IHybridCacheSerializer<>)` (undocumented). The Kit avoids it: it caches the value as a JSON `string` with `UsePouryaKitJson`.
- **`RemoveByTagAsync(IEnumerable)`** loops the single-tag overload: local invalidation first, then an L2 write that may throw and stop the loop. Evict tag by tag in a try/catch.
- **A tag invalidated while the factory runs** makes HybridCache bump the new entry's timestamp, so it stays valid: a query racing a commit can store its old answer until expiry. Documented as a limit.
- **Single-flight proof:** an `EventListener` on `Microsoft-Extensions-HybridCache` (Verbose) sees `StampedeJoin`; hold the first handler, `Task.WhenAny(joined, second)`, then release. A get-then-set mutation completes `second` instead, and runs the handler twice.
- **Redis:** HybridCache answers the first caller before its L2 write completes; poll `server.DatabaseSizeAsync()` before the second Host reads. Two Hosts composed but never started can run Gateway queries.
- **Mutation trap:** a "evict whatever the outcome" mutation placed inside the `ReferenceEquals(outcome, returned)` branch stays green for a save refused by the Unit of Work (`NameInUse`): that outcome is a new Result. Mutate right after `EndUnitOfWork()` instead.
- **Rebase onto a moved wave branch:** #51 and #42's fix round had added `params string[] arguments` to `AppFixture.ComposeHost`, `IIdempotencyStore` to the Kit-step rule, RabbitMQ to the counts. Resolve conflicts with a scratchpad python script; a `for f in ...; python3 -c` loop is refused.

**Why:** each decided the #43 shape or cost a run.
**How to apply:** any HybridCache use, any slot that runs Feature code inside a library callback, any single-flight or Redis test. Related: [[idempotency-facts]], [[continuation-trust-facts]], [[worktree-and-check-gotchas]].
