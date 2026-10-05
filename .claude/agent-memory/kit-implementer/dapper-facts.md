---
name: dapper-facts
description: Verified Dapper 2.1.89 / Npgsql 10.0.3 facts from ticket #9 (cancellation, type handlers, closure, seam traps)
metadata:
  type: project
---

Verified in ticket #9 (2026-10-05), Dapper 2.1.89, Npgsql 10.0.3:

- `NpgsqlDataSource.OpenConnectionAsync(token)` with an already-cancelled token returns an idle pooled connection without throwing; the Kit's `ReadConnectionFactory` calls `ThrowIfCancellationRequested` first.
- Dapper 2.1.89 `SqlMapper.AddTypeHandlerCore` takes a lock (source at commit eb47546); re-registering a type replaces the handler. No Kit-side lock needed. Positional records with typed-ID constructor parameters map fine once handlers exist.
- Npgsql runs a command inside the connection's active transaction even when `command.Transaction` is null, so a test cannot detect a seam that drops the transaction; only a separate connection would escape it.
- Npgsql's net10.0 closure is Logging.Abstractions -> DI.Abstractions only. Reading a connection string by name needs `Microsoft.Extensions.Configuration.Abstractions`, outside plan C's table: #9 added it after asking main by SendMessage; the owner approved it (2026-10-05). Asking main mid-package via SendMessage worked; the reply arrived after the first report.
- `git merge feat/wave-1` refuses while agent-memory files are modified: commit memory first.
- `ModuleBuilder` re-keys only `IUnitOfWork`; any other per-Module service registered with Add is last-wins Host-wide. Shared services (repository, `IUnitOfWorkConnection`) take the unkeyed `IUnitOfWork` (the router) with TryAdd.
- `PersistenceTests.Each_module_has_its_own_schema...` asserts the exact table list and migration names: any new table or migration edits it (merge-conflict hotspot with parallel tickets).
- A SQL-only EF migration (attributes `[DbContext]` + `[Migration]` on an internal class, no Designer) applies fine and leaves the snapshot unchanged.
- The worktree guard refused `curl ... $(curl ... | grep)` (it saw "git" inside a URL); run the two curls separately.

**Why:** each cost a run or a wrong claim in a provenance record.
**How to apply:** start from these when touching Persistence.Dapper, the Unit of Work seam, or the sample tests. Related: [[persistence-ef-facts]], [[worktree-and-check-gotchas]].
