---
name: final-review-web-facts
description: Wave 2 final review package A - Web's RestoreTrusted call, how to prove a peer continuation over HTTP, endpoint DisplayName without methods, narrow allowed_files vs the common docs-sync set
metadata:
  type: project
---

Verified in the Wave 2 final structural review, package A (2026-10-08, ASP.NET Core 10.0.12):

- **Web's trusted peer now calls `OperationContext.RestoreTrusted`** in `OperationContextHttpHeaders.Continued`; `TrustedRestoreTests` requires a Web call beside Messaging.Wolverine's. Before, it used the public constructor for weeks unnoticed: the #48 tests read only the context's fields, never `IsTrustedContinuation`. A mark that changes authorization needs a test that crosses the permission check.
- **Proving it over HTTP:** the sample's `CallerPermissions` grants only when the HTTP caller's Actor claim equals the context's Actor, so a peer continuing "ada" into `/stub/catalog-products/removal` (`RemoveProductsCommand`, permission-protected) passes only through the trust mark. Assert `deleted_by = 'ada'` and use a unique product name: the command removes every product with that name, and other classes create "Lamp".
- **Endpoint `DisplayName` without methods:** `routes.Map("/x", handler)` inside a Module group is just `/stub/x` (no `HTTP:` prefix), also after `.WithMetadata(new HttpMethodMetadata([]))`; `MapGet` gives `HTTP: GET /stub/x`. An empty `HttpMethodMetadata` is accepted and answers every method.
- **validate.py's log has no test counts**: it prints commands and the PASS line; take counts from your own verify runs.
- **Scope:** a final-review brief's `stop_if` ("any change outside the files below") wins over common.md's default docs-sync set; stale lines in `docs/architecture/README.md` go under `blockers` with their corrected text.
- The worktree guard refuses a `for` loop over `dotnet test` and a python heredoc that runs subprocesses: one command per call, or a script file in the scratchpad.

**Why:** these decided the package's shape or cost a run.
**How to apply:** for any Kit-only trust mark, any endpoint-build refusal test asserting a message, and any narrow fix package. Related: [[continuation-trust-facts]], [[misconfigured-host-facts]], [[security-permission-facts]].
