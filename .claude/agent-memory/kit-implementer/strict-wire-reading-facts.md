---
name: strict-wire-reading-facts
description: Ticket #60 facts - STJ strict options' exact behaviour, Wolverine's deserialization dead-letter path, two contract types under one wire name in one process, amq. prefix on RabbitMQ 4.3.6, SharedJsonTests trap
metadata:
  type: project
---

Verified in ticket #60 (2026-10-07), .NET 10.0.12 STJ, WolverineFx 6.46.0, RabbitMQ 4.3.6. Check the merged code first:

- **STJ strictness, probed:** `RespectRequiredConstructorParameters` makes every ctor parameter without a default value
  required, a nullable one too (`string? Note` with no default must be present). `RespectNullableAnnotations` refuses
  null into a non-nullable property or ctor parameter on read AND throws on write. A class of `init` properties without
  `required` (and base-record init props) still reads any shape with defaults. `JsonSerializerOptions.Strict` (.NET 10)
  = Disallow unmapped, no duplicate properties, case-sensitive, both Respect flags. Messages: "JSON deserialization for
  type 'Ns.T' was missing required properties including: 'name'." and "The constructor parameter 'Name' on type 'Ns.T'
  doesn't allow null values. ..." (full type name).
- **A file-based probe** (`dotnet run probe.cs`) defaults to `PublishAot`, so reflection STJ throws "Reflection-based
  serialization has been disabled": add `#:property PublishAot=false`. `#:package` restores from the cache offline.
- **Wolverine's read failure:** `HandlerPipeline.TryDeserializeEnvelope` catches and returns `MoveToErrorQueue` (no
  retries, no failure policy) -> `MessageContext.MoveToDeadLetterQueueAsync` -> `IMessageInbox.MoveToDeadLetterStorageAsync`
  (`Wolverine.RDBMS` `DatabasePersistence.ConfigureDeadLetterCommands`: id, body, message_type, received_at,
  exception_type, exception_message). Over the Kit's broker topology the read is at `rabbitmq://queue/{serviceName}`,
  before the fan-out. The EF outbox serializes at `EfCoreEnvelopeTransaction.PersistOutgoingAsync` (`OutgoingMessage`
  ctor), inside the commit. Decompiled copies: scratchpad `decomp58/wolverine`, `stj60/{rdbms,efc,wrmq}`.
- **Two contract types under one wire name, one process:** `HandlerGraph.TryFindMessageType` is per Host, so the Catalog
  Host can `Publish<OtherShape>(wireName, typeof(CatalogModule))` while Inventory.Host `Receive<Shape>(wireName)`; publish
  straight through `IMessageBus` with the DomainEventId header (`StrictWireReadingTests`). Keep the publishing Host alive
  until the envelope arrives (an `await using` inside an `if` block disposed it too early).
- **SharedJsonTests trap:** Domain is in `Sample.KitPackages`, so a Domain method that calls `UsePouryaKitJson` (the wire
  variant) shows up as a caller; exclude Domain from the caller listings.
- **RabbitMQ 4.3.6 `amq.`:** queue and exchange declares under `amq.` fail 403 ACCESS_REFUSED "contains reserved prefix
  'amq.*'"; `AMQ.` declares fine (case-sensitive). Wolverine's `RabbitMqExchange.CheckAsync` lower-cases the name. The Kit
  refuses any case (decision recorded in ADR 0006's #60 amendment; flagged as a design choice).
- Full ModularMonolith suite with strict wire reading on: no existing test broke (Domain Events, delayed commands,
  Snapshots all round-trip), about 2 min 10 s.

**Why:** each decided the #60 design or a test that would otherwise pass vacuously.
**How to apply:** for any later change to message contracts, JSON options or names. Related: [[shared-json-facts]],
[[rabbitmq-transport-facts]], [[messaging-delivery-facts]].
