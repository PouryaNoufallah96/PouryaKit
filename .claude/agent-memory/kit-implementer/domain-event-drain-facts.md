---
name: domain-event-drain-facts
description: Ticket #10 drain design (DomainEventDrain in Cqrs, rollback-only as the single failure signal) and the test tricks that made each guarantee mutation-proof
metadata:
  type: project
---

Ticket #10 (2026-10-05), check the merged code before relying on it:

- The drain lives in `PouryaKit.Cqrs` (`DomainEventDrain`, public, internal ctor, registered by `AddPouryaKitCqrs` with a factory) because `Operation.MarkRollbackOnly` is internal to Cqrs and EFCore has no InternalsVisibleTo. EF only supplies `ChangeTracker.Entries<IAggregateRoot>()`. A failed handler marks the Operation rollback-only, so `IUnitOfWork`'s contract ("command's failure or RollbackErrors") held unchanged; first failure wins.
- During the drain the outer command is still inside `Run` (`_runningCommands == 1`, UoW active), so a handler's nested command joins the same Operation and Unit of Work; command depth and failed-command rollback propagation still apply.
- Dispatcher backstop: a Unit of Work success while `IsRollbackOnly` throws `InvalidOperationException` (it may already have committed, so a failed Result would lie).
- Mutation tricks: a handler with `await Task.Yield()` exposes `Task.WhenAll`; declaring handlers out of name order exposes a dropped sort; a "must not run" handler that throws exposes a drain that does not stop; a raw-SQL insert inside the transaction makes "0 rows" prove a rollback (EF-added rows never reach the DB); the cycle test needs `[Fact(Timeout=...)]` plus a real `Task.Delay(…, ct)` in a handler, or a removed depth guard hangs instead of failing.
- `if (true)` in a mutation fails the build (CS0162 under TreatWarningsAsErrors); use a non-constant condition.
- `dotnet ef` from the worktree root needs absolute `--project`/`--startup-project` paths (relative ones fail with "Unable to retrieve project metadata").

**Why:** #11 (prepare persistence after the drain) and #15 (Outbox from the drained events, `RunAsync` returns them in order) build on this seam.
**How to apply:** start from these when touching the Unit of Work, Domain Event Handlers or their tests. Related: [[cqrs-design-facts]], [[persistence-ef-facts]].
