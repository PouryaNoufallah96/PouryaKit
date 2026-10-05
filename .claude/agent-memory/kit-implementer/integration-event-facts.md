---
name: integration-event-facts
description: Ticket #16 facts - process-global Wolverine wire names, extension checks fail at StartAsync, receipt-hold concurrency proof, envelope rows untouched until handled
metadata:
  type: project
---

Verified in ticket #16 (2026-10-05), WolverineFx 6.46.0; check the merged code before relying on the design:

- **Wolverine message type names are process-global.** `WolverineMessageNaming` caches `Type -> name` in a static `ImHashMap` and `Envelope.MessageType` comes from `GetType().ToMessageTypeName()`. A per-Host declared wire name (Integration Events: plain Contracts records, no Kit attribute) must go into a static map in `WireNaming`; a second Host in the process declaring another name is refused. Declare only after the Host's own checks pass.
- **`IWolverineExtension.Configure` runs inside the `WolverineOptions` singleton factory**, built when `IWolverineRuntime` (a hosted service) is resolved: an exception there reaches `host.StartAsync()` unwrapped, not `Build()`. Whole-Host checks (subscription nobody publishes, no Outbox) live there.
- **Concurrency proof without flakiness:** decorate `IReceipts` to hold the first delivery right after `RecordAsync` returns true (transaction open, Receipt uncommitted); `update wolverine_incoming_envelopes set owner_id = 0 where id = X` makes recovery hand out the same row again at once (local queues run `max(ProcessorCount, 5)` in parallel); wait until `pg_stat_activity` shows `wait_event_type = 'Lock'` on the receipts insert; repeat; release. Mutation "RecordAsync always true" gave 3 effects.
- A local durable envelope row is not rewritten when Wolverine starts handling it, only when marked Handled: `pg_xact_commit_timestamp(xmin)` read while the receiver is held still shows the publishing commit.
- `AssertNothingSentAsync` and `WaitForDeliveriesAsync` wait for the store to settle before taking their baseline (ticket #15's last fix), so a setup Operation's late deliveries are no longer charged to the Operation under test.
- A publisher that must reach the Outbox across packages: Kit seams are public interfaces "for a Kit package" (`IReceipts`, `IIntegrationEventOutbox`), never `InternalsVisibleTo`. The test `CommitProbe.Install` reads `ImplementationType`, so any further decorator of `IUnitOfWorkOutbox` must be installed after it and handle a factory descriptor.

- After the #15 fix round (merged 2026-10-05): subscription names are lower-cased in `Subscription`'s constructor, so duplicate-name messages print the lower-cased name; the Outbox and durable-queue checks live in one `StartupCheck` (an `IHostedLifecycleService.StartedAsync`, registered with `TryAddEnumerable` by every declaration call), keyed on the `INotificationOutbox` marker. Add any new whole-Host delivery check there, not in a second place.

- #16 fix round: `IUnitOfWork -> IUnitOfWorkOutbox -> IReceipts -> IUnitOfWork` is a cycle through factory registrations; MS DI does not detect it and the test run hung (no error) for 10 minutes. Resolve such a Kit service lazily from the scope. Run any new delivery test with `--timeout 180s` (MTP) in the background.
- A durable local queue hands the first attempt the published in-memory instance (checked with ReferenceEquals); a `ScheduleRetry` attempt is read back from the store, a new instance. A test that claims identity/time survive serialisation must assert on a retry or a recovered delivery.
- The restart proof for a receiver: hold in the test `IUnitOfWorkOutbox.FlushAsync` decorator (runs after commit, before the delivery returns, so before acknowledgment) by `Operation.Module`, `StopMode.Quick` the Host, start a new one; count `IReceipts.RecordAsync == false` to prove the new Host really handed it out again.
- Mutation code under TreatWarningsAsErrors: `if (false)` fails the build (CS0162); use `DateTime.Now.Year < 0`.

**Why:** each decides how #17/#18 extend delivery or would make a test pass vacuously.
**How to apply:** for dead letters (#17) and Snapshots (#18) through the same subscription registry and Outbox. Related: [[messaging-delivery-facts]], [[wolverine-spike-gotchas]].
