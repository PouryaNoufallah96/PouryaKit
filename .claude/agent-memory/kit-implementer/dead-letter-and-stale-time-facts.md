---
name: dead-letter-and-stale-time-facts
description: Ticket #17 facts - Wolverine 6.46 dead-letter replay mechanics, stale time against stored retries (reproduced), dying-Host proof without zombies, Receipts masking independence mutations
metadata:
  type: project
---

Verified in ticket #17 (2026-10-05), WolverineFx 6.46.0, decompiled with `ilspycmd` into the scratchpad (`wv/`); check the merged code before relying on the design:

- **Stored retry past the stale time is handed out concurrently.** `ScheduleRetry` writes the row back `Scheduled`, owner 0; the scheduled poll promotes it with `owner_id = node` and never touches `timestamp` (insert time). Once a delivery is older than `InboxStaleTime`, every attempt that spans a recovery poll is released and handed out again. Reproduced with no row edits in `InboxStaleTimeTests` (stale 500 ms, retry 1 s, hold after Receipt, wait for a `Lock` wait on `pouryakit_receipts`); a 5-min stale time is the control (no copy). The Receipt keeps one effect. Rule: stale time > sum of `RetryDelays` + every attempt's run and queue wait.
- **Replay** = `runtime.Storage.DeadLetters.ReplayAsync(new DeadLetterEnvelopeQuery([id]))` sets `replayable`; the durability agent's recovery batch inserts it into incoming with the same id (status Incoming, owner 0, attempts 0) and deletes the dead letter. A test waiting for settle must count `replayable` dead letters as pending. Re-inserting a dead letter while its Handled row (kept 5 min) exists would hit the `(id)` primary key; `runtime.Storage.Inbox.MoveToDeadLetterStorageAsync(envelope, ex)` is the clean way to set a delivery aside again (it deletes the incoming row first); `DeadLetterEnvelope.Envelope` keeps its `Destination`.
- `DeadLetterEnvelopeQuery { PageSize = 0 }` reads every dead letter; `ReceivedAt = "local://{subscription}/"` finds a subscription's.
- **Receipts mask independence mutations.** "Run every subscription per envelope" still applies each effect once, so assert on delivery attempts (`DeliveryProbes.AttemptsOf(subscription, messageId)`, recorded in the `IReceipts` decorator), not only handler runs.
- **Dying Host in-process without a zombie:** hold the delivery after its Receipt, `pg_terminate_backend` the `idle in transaction` session on `pouryakit_receipts` (a kill closes it), `StopMode.Quick` + `StopAsync` (no drain, does not wait for the held task), then `ReceiptHold.Abandon()` (never release); releasing would run the handler in the dead Host's scope.
- Per-Host singleton logs miss runs in a second Host; process-wide statics keyed by a unique text (`Stub/StubCirculation.cs`) see all.
- Mutation script `scratchpad/mutate17.py` (apply, run filtered classes or `m:` methods, restore). A mutated constant used in `Task.Delay` made one run take 5 min: filter to the target method.
- Adding a Module to the sample breaks `PersistenceTests` (schema and DbContext lists) and `NotificationTests.A_new_product_is_announced…` (exact delivery list).

**Why:** each decided a #17 test or would have made one pass vacuously.
**How to apply:** for #18 Snapshots (same delivery and Receipt path) and any later delivery or replay work. Related: [[messaging-delivery-facts]], [[integration-event-facts]], [[wolverine-spike-gotchas]].
