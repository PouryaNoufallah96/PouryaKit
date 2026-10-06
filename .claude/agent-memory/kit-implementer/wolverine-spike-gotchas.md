---
name: wolverine-spike-gotchas
description: Non-obvious WolverineFx 6.46.0 behaviours found in Spike S2 that bite Kit messaging code (codegen vs internal types, AddMessageHandler, EndpointFor status, stale-time retries)
metadata:
  type: project
---

Observed in Spike S2 (ticket #5, ADR 0006), WolverineFx 6.46.0, TypeLoadMode.Dynamic:

- Wolverine-generated handler code is compiled into an assembly with a random name (JasperFx.RuntimeCompiler 5.0.0 `AssemblyName ?? Path.GetRandomFileName()`), so a Wolverine handler type that names an `internal` type (even as a generic argument) fails with CS0122 at the first message, not at startup, and the message is dead-lettered. `InternalsVisibleTo("Internal.Generated")` does not help.
- `IMessageBus.EndpointFor(name).SendAsync` through an EF outbox to a durable local queue stores the row in `wolverine_incoming_envelopes` as `Outgoing`, owner 0; recovery only picks `Incoming`/owner 0, so it is lost after a kill. Use `PublishAsync` with a routing rule instead.
- `body` in the envelope tables is Wolverine's binary envelope, not UTF-8 JSON: `convert_from(body,'UTF8')` throws 22021; use `encode(body,'escape')` to regex it.
- `RetryWithCooldown` retries in memory; `attempts` stays 0 in the row, so counting restarts after a crash.
- Separated mode only separates when a message type has more than one handler; a lone handler goes to the message-type queue unless routed explicitly.
- Way around code generation: `opts.AddMessageHandler(type, new MyHandler())` with a `MessageHandler` subclass (WolverineOptions.cs:482). No codegen, so internal message types and handlers work; `OnException` policies still apply; middleware does not. ADR 0006 chose this (one `PublishMessage(type).ToLocalQueue(subscription)` route per subscription, subscription read from `Envelope.Destination`).
- Local queue names are lower-cased (`LocalTransport.QueueFor`); TCP destinations have host `localhost`, so map endpoint -> subscription by the endpoint (port), not by host.
- `InboxStaleTime` + in-memory `RetryWithCooldown` longer than it: the row is released, handed out again, and the in-memory retry still runs later, so a handler runs after it already succeeded. `ScheduleRetry` writes the row back as Scheduled, owner 0, attempts persisted, so it is skipped while waiting. Once promoted and running, it can be handed out again when its age from initial storage exceeds the stale time (`InboxStaleTimeTests`). `PouryaKitMessageStoreOptions.InboxStaleTime` must cover all retry delays plus every attempt's run and queue wait (ADR 0006, ticket #17 amendment).

- Default `UnknownMessageBehavior.LogOnly` marks a stored envelope Handled when its type has no handler any more (all subscriptions removed): silent loss. Set `DeadLetterQueue`.
- When counting owned lines for a comparison, count the file as it ran (whole-line removal of variant lines only), never an abridged snippet.

**Why:** these decide how #15/#16/#17 build delivery; each cost an investigation.
**How to apply:** check these before writing Kit delivery code or tests that read Wolverine tables.
