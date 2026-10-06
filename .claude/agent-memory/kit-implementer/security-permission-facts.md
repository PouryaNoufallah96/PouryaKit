---
name: security-permission-facts
description: Ticket #38 facts - fallback policy reach, testing a non-Module endpoint, why the sample's grants live in the fixture, rebasing onto a moved wave branch, validate.py from a worktree
metadata:
  type: project
---

Verified in ticket #38 (2026-10-07), ASP.NET Core 10.0.12:

- **The authorization `FallbackPolicy` also governs a path no route matches** (no endpoint at all): an anonymous request answers 401 instead of routing's 404; an authenticated one still 404. Pinned in `SecurityTests`.
- **Test a non-Module endpoint** by `AppFixture.ComposeHost(...)`, then `host.MapGet(...)` after `ModularMonolithHost.Build` and before `StartAsync`. Module route groups already `RequireAuthorization()`, so a fallback-policy test on a Module endpoint never goes red.
- `services.Configure<AuthorizationOptions>(o => o.FallbackPolicy ??= ...)` is the registration-time form; it keeps an application's own fallback set earlier and loses to one set later.
- **Permission source vs Gateway tests:** an `IActorPermissions` that reads `IHttpContextAccessor` grants nothing outside a request, so every Gateway test sending the protected command (as `GatewayTestExtensions.TestActor`) would be Forbidden. Pick the command whose existing call sites all run as TestActor (`RemoveProducts`, not `RenameProduct`, which one audit test sends as Person "bob"), and grant TestActor that one permission by name in a fixture-only wrapper (`TestActorPermissions`).
- Module commands are `internal`, so the Host cannot register a per-type authorizer: `AddPouryaKitPermissions(assembly)` goes in each Composition Root, as `AddPouryaKitValidation` does. A generic authorizer in the Security assembly is invisible to `AddPouryaKitCqrs`'s concreteness check.
- A Stub endpoint can drive a Catalog command over HTTP through `IGatewayProcessor<CatalogModule>` (InternalsVisibleTo); the Gateway ownership check accepts it because the command is Catalog's.
- **The wave branch moved twice mid-package** (#37 added `PouryaKit.Core`, then #34 rewrote `AddPouryaKitWeb`'s JSON line, a conflict beside the fallback policy). Check `git log HEAD..<branch>` right before reporting, not only before committing. Rebasing the one commit onto it kept "one commit"; both sides had changed `Assert.Equal(13, ...)` to 14, so git auto-merged a wrong 14 (must be 15). Recount every count line after such a merge, not only the conflicted files.
- `scripts/packaging/validate.py --no-fetch --output <scratchpad>` works from a worktree (~6 min) but needs a clean tree for its whole run, and this memory directory is tracked: write memory only after it finishes.

**Why:** each decided the #38 design or cost a round.
**How to apply:** for #39 (JWT, permissions from token claims), #40, and any ticket touching Web authorization or the package count. Related: [[web-aspnetcore-facts]], [[modularity-context-facts]], [[worktree-and-check-gotchas]].
