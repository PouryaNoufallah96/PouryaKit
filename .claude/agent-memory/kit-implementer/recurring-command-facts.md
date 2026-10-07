---
name: recurring-command-facts
description: Ticket #45 facts - Wolverine's own Schedules agent and why it was not used, ScheduledTime cleared at fire, ScheduledMessages query API, epoch-grid occurrences, fixture interference, Hold-based second-chain proof
metadata:
  type: project
---

Verified in ticket #45 (2026-10-07), WolverineFx 6.46.0; check the merged code before relying on the design:

- **Wolverine 6.46 ships recurring messages** (`opts.Schedules`, `RecurringMessageAgent`, a singular agent, cron via Cronos, 5 s minimum cadence, a `wolverine_recurring_messages` tracking table when `Durability.EnableRecurringMessages`). Not used: it publishes from its own loop outside any Unit of Work, so the next occurrence cannot commit with the effect, and it brings its own dedup policy. The Kit chains occurrences through the Outbox instead (ADR 0006 amendment for #45). Record this if a reviewer asks "why not the platform's".
- **`Envelope.ScheduledTime` is cleared when a scheduled envelope fires** (Wolverine's own doc comment on `RecurringMessage.OccurrenceHeaderKey`), so the occurrence time travels in the Kit header `DelayedCommandHeaders.DueAt`, written by `WolverineOutbox` on every delayed command.
- **Store query without SQL:** `runtime.Storage.ScheduledMessages.QueryAsync(new ScheduledMessageQuery { MessageType = type.ToMessageTypeName(), PageSize = 0 })` returns only `status = 'Scheduled'` rows (stored retries included), with `Destination` = `local://name/`; PageSize 0 = all. An occurrence promoted to `Incoming` and running is invisible to it, hence the second-chain limit.
- **Occurrences on an epoch grid** (`Recurrence.After`) make two chains collide on the same Receipt key; the turned-away delivery stores no next one, so chains collapse. Mutation "turned-away still stores next" stays red only if the test checks the waiting count before the grid collision collapses it (3 s cadence in the redelivery test).
- **Fixture interference:** every Host start seeds a `Scheduled` row; the fixture's reset deletes it, so only tests that start a second Host on the fixture DB and read all incoming rows break (`NotificationTests` died-before-sending: filter by queue). Also update `PersistenceTests` table list and `DapperWriteTests` Inventory literal list for a new plain-SQL table.
- **Second-chain proof:** `DeliveryProbes.HoldAfterReceipt(name)` holds the running occurrence inside its transaction, then start the second Host: it sees no `Scheduled` row and seeds. Deterministic.
- **Next stored in the same commit:** compare `pg_xact_commit_timestamp(xmin)` of the waiting `Scheduled` envelope and the effect row; only the untouched waiting row keeps its insert xmin (promotion/handling update it).
- `ValidateOnStart` on named options fails `host.StartAsync` before any hosted service runs, so a no-cadence test needs no database of its own.
- #47 landed mid-ticket: `ContinuedOperationContext` returns `(Context, TraceParent)` and `BeginOperation` takes a third `ActivityContext`; a recurring occurrence passes `default`, so its activity parents on Wolverine's span when sampled, else a new trace.
- Mutation script: `mutate45.py` in the session scratchpad (8 mutations, all red).

**Why:** each decided a #45 test or design point.
**How to apply:** for any change to recurring or delayed commands, the Outbox write, or tests that start extra Hosts. Related: [[delayed-command-facts]], [[dead-letter-and-stale-time-facts]], [[observability-facts]].
