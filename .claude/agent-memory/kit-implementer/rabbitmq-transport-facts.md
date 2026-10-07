---
name: rabbitmq-transport-facts
description: Ticket #51 facts - stub://replies listener, IBrokerEndpoint and DeadLetterStorage checks, Catalog IVT decides test placement, a handler context swallows send failures (F6)
metadata:
  type: project
---

Verified in ticket #51 (2026-10-07), WolverineFx.RabbitMQ 6.46.0, RabbitMQ 4.3.6. Check the merged code first:

- **Every Wolverine 6 Host has an Application-role listener `stub://replies/`** (BufferedInMemory, `StubTransport`). A
  "every non-local listener is durable" check refuses every existing Host. Filter broker listeners with
  `endpoint is IBrokerEndpoint` (`Wolverine.Transports`), which RabbitMqQueue implements and stub/local/tcp do not.
- **`Endpoint.DeadLetterStorage`** (virtual, core) is the transport-neutral "is there a native broker DLQ" signal:
  `RabbitMqQueue` returns Native when its `DeadLetterQueue` mode is not WolverineStorage. No RabbitMQ type needed.
- **Transport setup works inside an `IWolverineExtension`** (`UseRabbitMq(connectionString)` accepts an amqp:// URI
  through `ConnectionStringParser`). `UseRabbitMqUsingNamedConnection` registers an `IConnectionFactory` in
  `options.Services`, too late from an extension; read the connection string at registration instead.
- **`SystemTextJsonSerializer` exposes no `Options`**; prove the JSON by reading the stored broker row's body.
- **Sample Catalog's commands are internal with IVT to `ModularMonolith.Tests` only**, so broker tests that drive
  Catalog through its Gateway must live there (a new test project cannot send them without widening Catalog's csproj).
- **appsettings.json of a referenced Exe flows into the referencing test's bin** and can shadow the Web Host's; the
  proof Host keeps its appsettings uncopied and sets `RunWorkingDirectory` to its project directory.
- **Probe timing:** a Catalog Host plus Inventory Host pair on fresh databases and a vhost starts and delivers in about
  1.3 s per test; the whole 16-case broker class runs in about a minute.
- **Holding the gap between fan-out and the handled mark, with no Kit hook:** a `before update` trigger on
  `wolverine_incoming_envelopes` that runs `pg_advisory_xact_lock(n)` when a `rabbitmq:` row turns `Handled`, the lock
  held by the test's connection. Copies commit while the mark waits; `StopMode.Quick` stop returned at once with the
  mark still blocked; then `pg_terminate_backend` the `wait_event = 'advisory'` backends, drop the trigger, unlock,
  start a new Host. Count local copies with `message_type like 'catalog.%'`: the recurring `ReconcileStock` occurrence
  also sits on a local queue.
- **A child Host process:** `dotnet run --no-build --configuration <AssemblyConfigurationAttribute> --project <path>`
  with cwd at the repository root and config through env vars (`ConnectionStrings__app`) starts in about 2 s.
  Readiness: the broker queue's `ConsumerCount` > 0 (passive declare). `-- --help` means nothing to a generic Host: it
  starts and fails on its absent database.
- **`EndpointFor` to a durable local queue stores `Incoming`, owned by the node** (`DurableLocalQueue.StoreAndForwardAsync`,
  `PrepareForIncomingPersistence`), so a copy in flight at a kill is handed out again. The S2 `Outgoing`/owner 0 hole is
  only through an EF `DbContextOutbox`. `PublishAsync` from the fan-out would also hit the type's exchange route and
  loop forever through a stale binding.
- **A handler's own `MessageContext` swallows send failures (F6).** In a handler `Transaction == this`, so a send only
  joins `_outstanding`; `FlushOutgoingMessagesAsync` (run by `MessageSucceededContinuation` before `CompleteAsync`)
  catches each envelope's failure, logs "has been discarded" and never rethrows: a refused copy is lost while the
  envelope is marked handled. The fan-out sends through `new MessageBus(context.Runtime)` (Transaction null), whose
  `SendAsync` awaits `StoreAndForwardAsync` and throws after `DurableWriteRetry` (4 tries, ~400 ms). Proof technique: a
  `before insert` trigger on `wolverine_incoming_envelopes` that raises for one `received_at`; the dead letter's
  `exception_type` is `Npgsql.PostgresException`. Inventory.Host's own logs go to `RecordedLogs`, not the console.
- **Mutation trap:** `!await X | flag` parses as `(!await X) | flag`; write `!(flag | await X)` to make a gate accept
  everything. Three Guid v7 made in one millisecond do not sort in creation order (assert membership, not order).

**Why:** each cost a run or decided where code went in #51.
**How to apply:** for #52 (tracing over the broker) and any later transport. Related: [[rabbitmq-topology-spike-facts]],
[[messaging-delivery-facts]].
