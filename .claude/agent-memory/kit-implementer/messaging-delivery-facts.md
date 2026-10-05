---
name: messaging-delivery-facts
description: Ticket #15 facts - Wolverine 6.46 start/extension timing, store-polling test helpers, redelivery and unknown-subscription test tricks, xUnit assertion interface, EF1002
metadata:
  type: project
---

Verified in ticket #15 (2026-10-05), WolverineFx 6.46.0; check the merged code before relying on the design:

- **Wolverine refuses to publish before its Host starts** (`WolverineHasNotStartedException` from `MessageBus.PublishAsync`). Any test Host that sends an Operation raising a subscribed Domain Event must be started (`HostFixture.StartHostAsync`) and stopped or disposed. Two Solo Hosts on one store worked side by side in the serial test run.
- **Timing:** `UseWolverine(lambda)` runs the lambda at registration; `IWolverineExtension`s from DI run when the `WolverineOptions` singleton is first resolved (after every Composition Root). The message store (`PersistMessagesWithPostgresql`) must be eager; routes, `AddMessageHandler`, failure policies and durability settings can sit in an extension, which is how Modules added after `UsePouryaKitWolverine` (AppFixture's Stub) still get routes.
- **Tests read the store, not a tracked session:** a Wolverine `TrackedSession` completes on `ExecutionFinished`, before a `ScheduleRetry` fires. Pending = incoming rows with status <> 'Handled' plus outgoing rows; `KeepAfterMessageHandling` (5 min) keeps handled rows for the baseline diff. Respawn must exclude the store schema (its node tables belong to the live Host); delete envelopes and dead letters separately, after waiting until settled.
- **Redelivery trick:** `update wolverine_incoming_envelopes set status='Incoming', owner_id=0` on a handled row makes the recovery poll hand it out again (no dedupe; the no-receipt mutation doubled the effect). Rewriting `received_at` to an undeclared `local://name/` makes Wolverine build that queue on demand and the Kit throw `UnknownSubscriptionException`, which lands in `wolverine_dead_letters.exception_type`.
- **Between commit and flush:** a test-only decorator of `IUnitOfWorkOutbox` with an `AsyncLocal` probe (`tests/ModularMonolith.Tests/CommitProbe.cs`) reads commit timestamps or skips the flush; envelope rows are updated when handled, so compare `pg_xact_commit_timestamp(xmin)` before the flush.
- xUnit reports any exception implementing an interface named `IAssertionException`, in any namespace, as an assertion failure; `XunitException` lives in `xunit.v3.assert`, which `PouryaKit.Testing` does not reference.
- EF1002 (error under TreatWarningsAsErrors) flags an interpolated string passed straight to `ExecuteSqlRawAsync`; build the SQL in a local first.
- `IHostApplicationBuilder.UseWolverine` exists in 6.46; the Kit's entry is `builder.UsePouryaKitWolverine(connectionName: "app")`. `dotnet ef` with the Host as startup still works (the Host is built, never started).

**Why:** each cost a run or would make a delivery test pass vacuously.
**How to apply:** for #16 (Integration Events through the same outbox seam and delivery step), #17 (dead letters) and #18 (Snapshots via `IUnitOfWorkOutbox`). Related: [[wolverine-spike-gotchas]], [[wolverine-6-spike-facts]], [[persistence-preparation-facts]].
