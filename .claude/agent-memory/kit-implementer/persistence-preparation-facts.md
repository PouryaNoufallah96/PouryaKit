---
name: persistence-preparation-facts
description: Ticket #11 facts - EF savepoint hides rollback bugs, race-test technique, TestClock vs Gateway, dotnet-ef migrations remove trap, Except dedupes
metadata:
  type: project
---

Verified in ticket #11 (2026-10-05), EF Core 10.0.12 / Npgsql 10.0.3; check the merged code before relying on the design:

- **EF Core rolls back to its own savepoint when `SaveChanges` fails inside a user transaction.** A mutation "commit after a concurrency conflict" left no EF-tracked row behind, so EF-written rows never prove the Unit of Work rolled back. Prove it with a raw-SQL write on `IUnitOfWorkConnection` in the same Operation (Stub `HoldChangeCommand` writes a marker category).
- **Two-scope race technique:** a Stub command begun as a Catalog Operation (`BeginOperation(ctx, typeof(CatalogModule))` + `IDispatcher`) runs the real Feature nested, signals a `TaskCompletionSource`, then awaits a release. Wait for both signals before releasing the first, await it, then release the second. Holding *after* both nested changes ensures both Operations track the pre-commit row, so the loser conflicts at save. A spec query begun only after the winner commits may instead miss the row through the soft-delete filter.
- **TestClock never reaches a Gateway Operation:** the dispatcher rebinds `DomainClock` with the Host's `TimeProvider`. Build a second Host with `ModularMonolithHost.CreateBuilder([...connection string])` then `builder.Services.AddSingleton<TimeProvider>(clock.Time)` (Cqrs TryAdds; last wins). It may remain unstarted only for Operations needing no running hosted service. Notification, Integration Event or Snapshot delivery paths need a started Host; stop or dispose it afterward (`PersistencePreparationTests`).
- **`dotnet ef migrations remove` corrupted the Stub snapshot** because the last migration by ID was a hand-dated SQL-only one (no Designer). Never use remove there: delete the files and `git checkout` the snapshot. Rename a generated migration's timestamp (files + `[Migration]` id) to sort after hand-dated ones.
- Model snapshot drift (e.g. `IsConcurrencyToken`) makes `Migrate()` throw in EF 9+, so a convention change needs a migration in every DbContext, the test-only Stub's too (temporary `IDesignTimeDbContextFactory` + temporary Design package, both removed after).
- `Enumerable.Except` is a set operation: it silently deduplicates the left side.
- `CascadeDeleteTiming.OnSaveChanges` is what keeps a soft-deleted aggregate's owned parts and children; with `Immediate`, `Remove` marks them Deleted before the prepare step and the save fails.
- #11 fix round (review F1): the walk reads a child's holder from both the current and the original FK value, so a moved child bumps both aggregates and a nulled optional key bumps the one it left; an untracked holder throws (an unloaded aggregate cannot conflict). The Stub `Folder` has pages (required key), stickers (optional key) and `MovePage`; EF reparents a child moved between two tracked collections as an FK update, not an orphan delete.
- `PouryaKitDbContext`'s constructor touches `ChangeTracker`, which builds the model: a model-convention refusal throws from `new MyContext()`, not from `.Model`.
- Converting a removed soft-deletable entry Deleted -> Unchanged keeps the Operation's earlier changes to it (checked: a rename before removal was saved).
- `AppFixture.RowsAsync` returns `DBNull.Value` for SQL null, not null.
- #11 second fix round (F5): with `CascadeDeleteTiming.OnSaveChanges`, EF cascades hard deletes inside `SaveChanges` (dependent Deleted, or optional key cleared -> Modified), after the prepare step. The prepare step now calls `ChangeTracker.CascadeChanges()` after the soft-delete conversion. `CascadeChanges` also deletes severed orphans, so `DeleteOrphansTiming` must stay `Immediate` (pinned in `PouryaKitDbContext`): orphans are then Deleted at the prepare step's first DetectChanges and a soft-deletable one is converted.
- Observing save-time mutation honestly: subscribe `DbContext.SavingChanges` (raised before the save's DetectChanges and cascades) and `ChangeTracker.StateChanged`, and record any transition to Added/Modified/Deleted after saving began (Stub `EditTrayCommand`). Accepting changes after the save goes only to Unchanged/Detached.
- An EF base type with `ToTable` and derived types configured without one defaulted to one table per derived type in EF 10 here; say `UseTphMappingStrategy()` to get one table. Keys to sibling derived types share the base's `IKey` (F6).
- A domain transition that raises an event (Product.Remove) needs its own once-guard: a removed aggregate is still found by later queries in the Operation until the prepare step marks it.

**Why:** each cost a run or would have let a test pass vacuously.
**How to apply:** when touching the Unit of Work, conflicts, Snapshots (#18) or test migrations. Related: [[persistence-ef-facts]], [[domain-event-drain-facts]].
