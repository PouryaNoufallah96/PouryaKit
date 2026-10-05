---
name: wolverine-6-spike-facts
description: Non-obvious WolverineFx 6.46 and PostgreSQL behaviours found in Spike S1 that bite when writing Kit tests or registrations
metadata:
  type: project
---

These facts were found in Spike S1 (ticket #2, 2026-10-05). The authoritative record is `docs/adr/0004-*.md`; check it is still current before relying on this.

- Wolverine 6 needs `WolverineFx.RuntimeCompilation`, or the Host fails at start (TypeLoadMode.Dynamic has no IAssemblyGenerator).
- With handlers outside the entry assembly (always the case under a test runner), call `opts.Discovery.IncludeAssembly(...)`.
- To prove "same transaction", compare `pg_xact_commit_timestamp(xmin)`, not `xmin`. EF wraps `SaveChanges` inside a user transaction in a savepoint, so the rows get different xids. The container needs `-c track_commit_timestamp=on`.
- A killed node's envelopes take about 75 s to recover with Balanced default timers. Tests should shorten the `Durability.*` timers or use Solo mode.
- If a Notification is committed but not flushed while its Host stays alive, it is stuck until the Host stops. Recovery takes only rows with `owner_id = 0`. `InboxStaleTime` releases the stuck row, but it also re-dispatches rows whose handlers are still running and have been running longer than the stale time.
- An `IDbContextOutbox<T>` instance is good for one commit attempt only. A rolled-back envelope stays in its outstanding list. With the default `MultiFlushMode` of `OnlyOnce`, a second flush sends nothing.
- Disposing a Host logs `ObjectDisposedException` errors from Wolverine's DatabaseBatcher. They are noise.

**Why:** each of these cost a debugging round or would silently weaken a test.

**How to apply:** use these when writing #15, #16 or Testing-package code and tests. Related: [[worktree-dotnet-gotchas]].
