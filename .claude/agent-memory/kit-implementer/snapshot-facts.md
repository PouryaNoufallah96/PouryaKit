---
name: snapshot-facts
description: Ticket #18 facts - Snapshot step 4 in the Outbox write, version gate beside the Receipt, test traps where the Receipt masks the gate, mutation results
metadata:
  type: project
---

Ticket #18 (2026-10-05), WolverineFx 6.46.0, EF Core 10.0.12; check the merged code before relying on the design:

- **Step 4 lives in the Outbox write.** `PersistencePreparation.Run` returns its `changed` list; `IUnitOfWorkOutbox.WriteAsync` got a `changedAggregates` parameter; `SnapshotStep` (Wolverine.EFCore) maps them through the public seam `ISnapshotMappers` (Messaging.Wolverine). Any new decorator of `IUnitOfWorkOutbox` (tests' `CommitProbe`, `DeliveryProbes`) must pass the list through.
- **Version gate:** `IReceipts.RecordVersionAsync` = one `insert ... on conflict (subscription, aggregate_id) do update ... where applied.version < excluded.version` on `pouryakit_snapshot_versions` (created by `CreateSnapshotVersionTable(schema)` in a hand-written migration). PostgreSQL waits on the conflicting row and re-checks the WHERE against what committed, so it holds under concurrency.
- **The Receipt masks the gate in redelivery tests.** Redelivering a handled envelope stops at its Receipt before the gate. Wipe the receiver's copy, version row and that subscription's Receipts first (`ForgetCopiesAsync` in `SnapshotTests`), then redeliver in the order wanted. `HoldAfterReceipt` holds before the gate; use `DeliveryProbes.HoldAfterVersion` for the race.
- **Mutations that proved the tests:** no `where` -> reverse order, after-Tombstone and newer-first concurrency fail; select-then-upsert -> only newer-first concurrency fails; no `DistinctBy` in prepare -> 11 fail; snapshot before prepare -> 13 fail; no mapper guard -> the mapper test fails; a random identity per delivery -> only the duplicate test's Receipt count fails (the gate alone hides it). Mutation runner: scratchpad `mutate.py` pattern (copy, replace, run `--filter-class`, restore in `finally`).
- Wolverine's envelope `body` column is its binary envelope format, not message JSON: identify a Snapshot's envelope by diffing envelope ids per step, and assert content through a recording handler or the receiver's table.
- A key the database generates is temporary at step 4; `SnapshotStep` throws for it. The Stub `Note` (long, `ValueGeneratedOnAdd`) therefore has no Snapshot.
- Adding a subscription to Product changes exact delivery lists (`NotificationTests`) and unfiltered `inventory.pouryakit_receipts` counts (`IntegrationEventTests`); filter by subscription.

- Merging #16's fix round and #17 (2026-10-05): a new subscription on Product reorders Inventory's commits (its Snapshot delivery commits before the Integration Event's), so a test hold keyed on the Module alone catches the wrong delivery; `DeliveryProbes.HoldAfterCommit` now takes the subscription. Unfiltered Receipt queries across a Module's subscriptions break the same way. A failed hold test once knocked on into `InboxStaleTimeTests`; it passed in four clean runs after.
- The Outbox resolves `IReceipts` from the scope at use time (a constructor dependency made an undetected cycle and hung the run); `ISnapshotMappers` in the Outbox's constructor is safe because its mappers are resolved at map time.

- #18 fix round (2026-10-06): the mapper guard must copy each value with `property.GetValueComparer().Snapshot(...)` (a `PropertyValues.Clone()` shares a `List<string>`) and walk `GetFlattenedProperties()` (complex properties are not in `GetProperties()`); `entry.CurrentValues[property]` reads a complex type's nested property fine in EF 10. Declarations are matched by the Operation's Module. A test that reads the singleton `IntegrationEventLog` must filter to its own aggregates: a mutation that lets another test's Operation commit leaks entries into it.
- The shared scratchpad is shared with other agents of the session: another agent overwrote `mutate.py`; name scratch scripts per ticket (`mutate18.py`).
- Generating a Stub migration: a temporary `IDesignTimeDbContextFactory<StubDbContext>` that builds `AppFixture.ComposeHost("Development", "Host=localhost;...", new RecordedLogs())` and resolves the DbContext from a scope, plus a temporary `Microsoft.EntityFrameworkCore.Design` reference in the test csproj; `dotnet ef migrations add X --project <abs csproj> --startup-project <abs csproj> --context StubDbContext --output-dir Stub/Migrations`; then delete both.

**Why:** each would let a Snapshot test pass vacuously or cost a run.
**How to apply:** when touching Snapshots, the Outbox seam, Receipts or the delivery step (#17 dead letters, #19 architecture tests). Related: [[integration-event-facts]], [[messaging-delivery-facts]], [[persistence-preparation-facts]].
