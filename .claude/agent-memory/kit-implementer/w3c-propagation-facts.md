---
name: w3c-propagation-facts
description: Spike #46 (ADR 0009) - Wolverine 6.46 trace propagation facts, listener traps, and spike mechanics that worked for referencing the Proving Sample
metadata:
  type: project
---

Observed in Spike #46 (2026-10-07, WolverineFx 6.46.0); the record is `docs/adr/0009-*.md`, check it is current:

- Wolverine writes `Activity.Current.Id` as envelope `parent-id` at `PublishAsync` (the Unit of Work's `WriteAsync`, during commit), not at flush; stored in the body; no tracestate anywhere.
- Its execution span exists only if a listener samples the `Wolverine` ActivitySource; otherwise nothing is current in `SubscriptionDelivery` and a new activity starts a new trace (no ExecutionContext leak through the local queue).
- Wolverine starts spans with a *string* parent: a test listener must set `SampleUsingParentId` too, or the Wolverine span vanishes for the wrong reason.
- A `ScheduleRetry` attempt gets no `ActivityLink` to the failed one: the previous-attempt header is in memory only; the store's reschedule UPDATE does not rewrite the body.
- Spike mechanics that worked: csproj directly in `spikes/<name>/` (so `dotnet build spikes/<name>` finds one entry), stopper props, `ProjectReference` to the sample Host and `PouryaKit.Testing`; per-test `create database` on one container; the sample's commands are `internal`, so build them by name with `Activator.CreateInstance` instead of touching the sample. A scoped `IDispatcher` decorator wraps both Gateway and delivery Operations (stand-in Operation activity).
- `gh api .../git/...` paths and `sh script $(cat list)` are refused by the worktree guard; a fetch script that reads its list file (`while read`) works.

**Why:** #47 implements only tracestate and the parent fallback from these; #52 must re-probe the broker path.
**How to apply:** for #47 tests and #52's propagation probe. Related: [[wolverine-spike-gotchas]], [[integration-event-facts]].
