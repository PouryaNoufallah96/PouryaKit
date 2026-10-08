---
name: sent-envelope-capture-facts
description: Wave 2 final-review package D - seeing broker envelopes the store deletes once sent (wolverine-messages-sent on the runtime Meter), latched sender path, PauseAsync as the unavailable broker, a HostFixture with a broker, one record per Unit of Work DbContext
metadata:
  type: project
---

Verified 2026-10-08, WolverineFx 6.46.0 (decompiled under the session scratchpad `decomp58/wolverine`); check the merged code first:

- **An outgoing broker envelope is deleted once sent**, so a store read after the Operation misses it. Wolverine calls `IMessageTracker.Sent` when a sending agent takes the envelope (`SendingAgent.EnqueueOutgoingAsync`/`StoreAndForwardAsync`), inside the Unit of Work's awaited flush, before the broker send and the row delete. `WolverineRuntime.Sent` always adds 1 to the `wolverine-messages-sent` counter (`MetricsConstants.MessagesSent`) on `IWolverineRuntime.Meter`, a Meter per runtime, tagged `message.type` and `message.destination`. A `MeterListener` filtered by `instrument.Meter == runtime.Meter` sees one Host only. System endpoints use the `SystemTraffic` tracker (no counter); durable local queue sends are counted too (filter `local:`).
- **A latched sender hands nothing out**: `Envelope.QuickSendAsync` (the DbContextOutbox flush path, `Transaction != this`) calls `MarkProcessingFailureAsync` instead of `EnqueueOutgoingAsync` when `Sender.Latched`, so no `Sent`; the outgoing row stays. Both reads are needed. `SendingAgent.PauseAsync(TimeSpan)` (public; agent from `runtime.Endpoints.GetOrBuildSendingAgent(uri)`) latches it as an unavailable broker does, then resumes.
- `TrackedSession`/`ActiveSession` are internal; `TrackActivity().ExecuteAndWaitAsync` waits for handlers. `IWireTap` needs per-endpoint `UseWireTap()`.
- **A HostFixture with a broker:** a private subclass taking `environment.Broker`, `CreateHost` = `AppFixture.ComposeHost(..., $"--ConnectionStrings:broker={broker}")`, created and `InitializeAsync`'d in the test (own PostgreSQL, about 3 s). `RenameProductCommand` there writes only the Snapshot's broker envelope (`catalog.product-snapshot.v1` to `rabbitmq://exchange/catalog.product-snapshot.v1`). To force "sent and deleted before the Operation returns", await `WaitUntilDeliveredAsync` inside the operation lambda.
- **Mirror defect left open:** `WaitForDeliveriesAsync` still throws "sent nothing" on an outgoing-only publication.
- **One record per Unit of Work DbContext:** `AddPouryaKitEfCore<T>(connectionName = null)` records `DbContextConnection(string? Name, Type)` once; `UsePostgres` passes its name. Since the D fix round a name replaces an unnamed record (index set, `services[services.IndexOf(d)] = ...`, safe through `ModuleBuilder`) and two names throw. The missing-string check (`DbContextConnectionsCheck`, Persistence.EFCore) must skip a null name (`GetConnectionString(null)` reads `ConnectionStrings:` and reports a false missing line).
- **A check that guards a record is registered where the record is made** (D review F1, high): the record was public-API reachable (`AddPouryaKitEfCore(name)`) but its check was registered only by `UsePostgres`, and the other check skipped the case trusting it. When one check skips a case "because check X reports it", prove X is registered on every path that creates the case.

**Why:** each decided a design point of the final-review package D.
**How to apply:** for any negative delivery assertion, broker-unavailable test, or composition check over Unit of Work DbContexts. Related: [[store-database-check-facts]], [[test-gaps-and-store-database-facts]], [[rabbitmq-transport-facts]], [[messaging-delivery-facts]].
