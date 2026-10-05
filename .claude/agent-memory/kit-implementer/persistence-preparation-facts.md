---
name: persistence-preparation-facts
description: Ticket #11 facts - EF savepoint hides rollback bugs, race-test technique, TestClock vs Gateway, dotnet-ef migrations remove trap, Except dedupes
metadata:
  type: project
---

Verified in ticket #11 (2026-10-05), EF Core 10.0.12 / Npgsql 10.0.3; check the merged code before relying on the design:

- **EF Core rolls back to its own savepoint when `SaveChanges` fails inside a user transaction.** A mutation "commit after a concurrency conflict" left no EF-tracked row behind, so EF-written rows never prove the Unit of Work rolled back. Prove it with a raw-SQL write on `IUnitOfWorkConnection` in the same Operation (Stub `HoldChangeCommand` writes a marker category).
- **Two-scope race technique:** a Stub command begun as a Catalog Operation (`BeginOperation(ctx, typeof(CatalogModule))` + `IDispatcher`) runs the real Feature nested, signals a `TaskCompletionSource`, then awaits a release. Release the first, await it, then release the second. Hold *after* the nested change: once the winner commits, the soft-delete filter hides the row from the loser's spec query and it never conflicts.
- **TestClock never reaches a Gateway Operation:** the dispatcher rebinds `DomainClock` with the Host's `TimeProvider`. Build a second Host with `ModularMonolithHost.CreateBuilder([...connection string])` then `builder.Services.AddSingleton<TimeProvider>(clock.Time)` (Cqrs TryAdds; last wins). No need to start it.
- **`dotnet ef migrations remove` corrupted the Stub snapshot** because the last migration by ID was a hand-dated SQL-only one (no Designer). Never use remove there: delete the files and `git checkout` the snapshot. Rename a generated migration's timestamp (files + `[Migration]` id) to sort after hand-dated ones.
- Model snapshot drift (e.g. `IsConcurrencyToken`) makes `Migrate()` throw in EF 9+, so a convention change needs a migration in every DbContext, the test-only Stub's too (temporary `IDesignTimeDbContextFactory` + temporary Design package, both removed after).
- `Enumerable.Except` is a set operation: it silently deduplicates the left side.
- `CascadeDeleteTiming.OnSaveChanges` is what keeps a soft-deleted aggregate's owned parts and children; with `Immediate`, `Remove` marks them Deleted before the prepare step and the save fails.
- An optional child FK turns an orphan into an FK-null update the bottom-up "which aggregate" walk cannot attribute; aggregate children need a required FK.

**Why:** each cost a run or would have let a test pass vacuously.
**How to apply:** when touching the Unit of Work, conflicts, Snapshots (#18) or test migrations. Related: [[persistence-ef-facts]], [[domain-event-drain-facts]].
