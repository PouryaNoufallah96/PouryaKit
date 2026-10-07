---
name: idempotency-retention-facts
description: #42 follow-up facts - .NET 10 BackgroundService runs ExecuteAsync on Task.Run, PeriodicTimer on FakeTimeProvider, a tick failure stops the Host, enumerating DbContexts, to_regclass skip, invariant log formatting, mutation compile traps
metadata:
  type: project
---

Verified in the #42 follow-up (2026-10-07), Microsoft.Extensions.Hosting.Abstractions 10.0.12 (decompiled with ilspycmd), EF Core 10.0.12, PostgreSQL 18.6. Check the merged code first (`IdempotencyKeyCleanup`, `IdempotencyRetentionTests`):

- **.NET 10 `BackgroundService.StartAsync` runs `ExecuteAsync` with `Task.Run`**, so nothing in `ExecuteAsync` has happened when `host.StartAsync` returns. A `PeriodicTimer` created there races a test's `FakeTimeProvider.Advance`; create it in a `StartAsync` override before `base.StartAsync`.
- `new PeriodicTimer(interval, timeProvider)` with the test clock fires on `Advance`; advancing several periods coalesces into one or two ticks. The tick's work is asynchronous: wait on its effect (a row gone, a log entry), never on `Advance` returning.
- **An exception escaping `ExecuteAsync` stops the Host** (`BackgroundServiceExceptionBehavior.StopHost`). Catch per tick with `when (!stoppingToken.IsCancellationRequested)`. Proof: rename the column the tick reads with `Fixture.RowsAsync("alter table … rename column …")`, restore it in `finally`, then assert the next tick works and `ApplicationStopping` is not cancelled.
- **Finding every Module's DbContext from a package that cannot see Modularity:** `AddDbContext` also registers one non-generic `DbContextOptions` per context (with `Add`), so `scope.GetServices<DbContextOptions>().Select(o => o.ContextType)` lists them, as `HostFixture.StartHostAsync` does. Filter by `PouryaKitDbContext`.
- **A Module without a Kit table:** check `select to_regclass({0}) is not null as "Value"` first; a statement on a missing table makes EF log `Failed executing DbCommand` at Error level on every tick.
- **`[LoggerMessage]` formats values with the invariant culture**, while a test's interpolated string uses the current one: put `:O` in the template (`{Cutoff:O}`) and in the test.
- `ValidateOnStart` makes `host.StartAsync` throw `OptionsValidationException` with every message in `Failures`, before any hosted service or migration, so such a test needs no database. Register the validator with `TryAddEnumerable` when the registration runs once per Module.
- **Mutation compile traps:** dropping the only use of a primary-constructor parameter fails CS9113 (keep a read: `(timeProvider is null ? timeProvider! : TimeProvider.System)`); a catch filter like `exception is null` makes `exception` maybe-null (CS8604). `mutate42f.py` in the session scratchpad ran 7 mutations, all red.
- Default retention source: Stripe API reference, "Idempotent requests" (24 hours, a reused key after pruning is a new request), recorded in `docs/provenance/PouryaKit.Persistence.EFCore.md`.

**Why:** each decided the retention design or a test of it.
**How to apply:** any Kit `BackgroundService`, any test that drives one with the test clock, any Kit table cleaned across Modules. Related: [[idempotency-facts]], [[recurring-command-facts]], [[worktree-and-check-gotchas]].
