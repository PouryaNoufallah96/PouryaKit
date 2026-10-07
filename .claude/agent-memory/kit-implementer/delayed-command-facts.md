---
name: delayed-command-facts
description: Ticket #44 facts - Wolverine 6.46 scheduled envelopes through the EF outbox, system-clock firing, restart proof on a database of its own, mutation results, why the delayed-command registry is separate
metadata:
  type: project
---

Verified in ticket #44 (2026-10-07), WolverineFx 6.46.0; check the merged code before relying on the design:

- **Scheduled publish through the EF outbox works as is.** `DeliveryOptions { ScheduledTime = at }` makes `DeliveryOptions.Override` set `Status = Scheduled`; `DbContextOutbox.PublishAsync` writes the incoming row in the Unit of Work's transaction with status `Scheduled`, `owner_id` 0, `execution_time` = at, same commit timestamp as the business row. The flush does nothing for a durable sender ("relying on durable inbox scheduling"); the scheduled-job poll promotes it, same envelope id.
- **Firing uses `DateTimeOffset.UtcNow`, not `TimeProvider`.** Only `RecurringMessageAgent` (Wolverine.Runtime.Recurring, relevant to #45) reads a TimeProvider. Tests use short real delays (300 ms to 3 s with the 100 ms test polling); a FakeTimeProvider on the Host would put the due time in fake time.
- **`AddMessageHandler(type, handler)` sets `handler.Chain`** (WolverineOptions.cs:1022): one `MessageHandler` instance per message type, never shared, or every type gets the last one's chain. No behaviour test shows it while all chains carry the same global policies.
- **A delayed command due later than `InboxStaleTime` is stale from its first attempt.** Promotion (`PostgresqlMessageStore` `_reassignIncomingSql`, V6.46.0 lines 132-137) sets owner and `Incoming` but keeps `timestamp` (insert time); the stale bump releases owned rows by that time. The Receipt keeps one effect; documented in ADR 0006's #44 amendment, not pinned by a test.
- **Pending vs not yet due (fix round F2):** a stored retry is also a future `Scheduled` row, so "skip Scheduled rows not yet due" broke a dozen retry tests at once. The rule is `status = 'Scheduled' and attempts = 0 and execution_time > now()` = a delayed command never attempted; a retry has `attempts > 0`. `StoredDeliveriesSchemaTests`' hand-made tables need `attempts` and `execution_time` columns. A test needing the effect now waits on the row's `Handled` status itself.
- **Rename simulation (F3):** moving a not-yet-due `Scheduled` row's `received_at` to an undeclared `local://x/` before it fires: the scheduled poll promotes it there, Wolverine builds the queue on demand, and the type's handler gets `Destination.Host = x`.
- **A refused save after the Outbox step (F4):** a second `OpenQuote` of one id fails on `pk_quotes` with `DbUpdateException`, after step 8 wrote the expiry; the bus-publish mutation leaves a second row.
- `Subscriptions.Check` is the one name rule (made public in the internal class) for wire, subscription and delayed command names.
- **Restart proof needs a database of its own** (`create database` on the fixture's container, `NpgsqlConnectionStringBuilder { Database }`): a scheduled row has owner 0, so the fixture Host, alive throughout, would run it. `HostFixture.StartHostAsync` migrates the new database.
- **Context per attempt:** `DeliveryProbes.ContextsOf(subscription, messageId)` records the scope's `OperationContext` in the `IReceipts` decorator.
- **Mutations that went red:** Outbox drops delayed commands (no row); no `ScheduledTime` (row `Incoming`, ran at once); delivery as `Actor.System` (`last_changed_by`); no Receipt check (second expiry fails, dead letter); `Schedule` through `IMessageBus.PublishAsync` instead of the Outbox (rolled-back Operation leaves a row). `mutate44.py` in the session scratchpad.
- **Why a separate registry:** the brief kept #49's files (`Subscriptions`, `Subscription`, `PouryaKitWolverineExtension`, `WireNaming`) out of reach, so delayed commands have `DelayedCommandDeclarations`, `DelayedCommandExtension` and `DelayedCommandDelivery`, and Wolverine's default full-type-name naming. Folding them into `Subscription` later is possible.
- The contract `IDelayedCommands` lives in Messaging.Wolverine, not Messaging: `Command` is in Cqrs, and section C gives Messaging only Domain.
- Shell: the worktree guard refused a `cat > file <<EOF` that named a path under the worktree; write files with the Write tool, edit with a scratchpad python script.

**Why:** each decided a #44 test or design point; #45 (recurring work) builds on the same store and delivery.
**How to apply:** for #45 and any change to delayed commands or the Outbox write. Related: [[wolverine-spike-gotchas]], [[messaging-delivery-facts]], [[dead-letter-and-stale-time-facts]].
