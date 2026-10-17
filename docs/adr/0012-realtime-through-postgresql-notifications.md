---
status: accepted
---

# A realtime push is a PostgreSQL notification on its delivery's own transaction, heard by one listener per instance

Spec #115 decided Realtime's mechanism between instances (R1) and ticket #133 built it in `PouryaKit.Realtime`. **A
Notification Handler pushes a realtime event with `IRealtimePublisher`, which runs `pg_notify` on the delivery's own
transaction, the EF Unit of Work's; PostgreSQL delivers the notification to every session that listens on the channel
only when that transaction commits; each instance of the Host holds one dedicated listening connection and passes each
push to its own clients of the delivery's tenant that listen on the push's Channel, through an SSE endpoint a Module maps
with `MapPouryaKitRealtime`.** No process holds a list of another's clients, and no event is stored: delivery to a client
is at most once, while the client is connected, and the durable state stays in the Outbox and the Receipts, which a
client reads through a query. Nothing observed contradicts ADR 0001 to 0011 or section G.

## Context

Plan I.6 answer 4 put realtime push in one optional package used from Messaging: a Notification Handler pushes after
commit, a client connected to any instance receives the event, through SSE, with no SignalR package and no second push
channel. The plan left the mechanism between instances open (2026-10-09): a Notification is delivered to its handler
once, on one instance, so a client connected to another instance needs something between instances. Both reference
managers keep their connections in a dictionary of one process (`Ultrabusiness@d685de6
src/BuildingBlocks/BuildingBlocks.SSE/Services/SseConnectionManager.cs:22`, `cybernod@bae8ac3
src/BuildingBlocks/BuildingBlocks.SignalR/Implementations/SignalRConnectionManager.cs:23`), which loses every push to a
client of another instance.

## Decision

- **The push is `pg_notify` on the Unit of Work's transaction.** `IRealtimePublisher.PublishAsync(channel, event)` writes
  `select pg_notify('pouryakit_realtime', payload)` through `IUnitOfWorkConnection`, so it commits or rolls back with the
  delivery's Receipt and its effects. PostgreSQL's `NOTIFY` documentation: "if a NOTIFY is executed inside a transaction,
  the notify events are not delivered until and unless the transaction is committed". A rolled-back delivery, a failed
  commit included, pushes nothing; a retry pushes again, and only the attempt that commits is heard. A delivery whose
  commit was lost before its acknowledgement is handed out again, finds its Receipt and runs nothing, so a committed push
  is never repeated.
- **The payload is the Channel, the tenant and the event, in the Kit's JSON**, on one PostgreSQL channel,
  `pouryakit_realtime`; the Kit's Channel is a field of the payload. The tenant is the delivery's Operation Context's, or
  `OperationContext.NoTenant`. A payload of 8000 bytes or more fails the push with `RealtimeErrors.PayloadTooLarge`
  before anything is written: the documentation says the payload "must be shorter than 8000 bytes" in the default
  configuration. The handler meets it, never the transport.
- **"Callable only from a Notification Handler's delivery" is decided from the Operation.** A delivery's Operation is a
  trusted continuation, restored from the headers its Outbox wrote (ADR 0007), running in a Unit of Work, where it may
  change state; the publisher throws anywhere else, as the Integration Event publisher does. The Operation does not show
  which kind of delivery it is, so the check also admits an Integration Event Handler's or a delayed command's delivery,
  a trusted peer's request and a command another Module's handler sends with `Continue()`. Each commits or rolls back
  with its push, which is the guarantee; telling a Notification Handler apart would need a public seam in
  `PouryaKit.Messaging.Wolverine`, which Realtime does not reference.
- **One listener per instance.** A hosted service opens one connection over the connection string `AddPouryaKitRealtime`
  names, outside every pool and never multiplexed, since a pooled connection is reset when it returns and a multiplexed
  one holds no session; with `KeepAlive`, so a connection the network dropped fails its wait; and runs `LISTEN` on it
  before the Host serves. When the connection is lost, the listener reconnects, retrying with a delay that doubles from
  1 to 30 seconds, listens again, and then sends every connected client a `gap` event: pushes committed while it was
  not listening are lost, and a client that reads its state on `gap` misses nothing committed afterwards.
- **The clients are the instance's, in memory, keyed by tenant and Channel.** The endpoint takes the tenant and the
  Actor from the request's Operation Context, which the Module's route group begins and authenticates; the endpoint's
  rule admits each requested Channel for the Actor, or the request is refused with a problem response before anything
  streams. The response is the framework's `TypedResults.ServerSentEvents`, the shape learned from
  `Ultrabusiness@d685de6 src/BuildingBlocks/BuildingBlocks.SSE/Extensions/SseEndpointExtensions.cs:58`; it is served by
  the request, never from inside a Gateway call, so no Operation stays open while it lasts. A client a full buffer of
  256 events behind is disconnected, since the listener never waits for one.
- **A build-time check holds the listener and the Units of Work to one connection string.** PostgreSQL delivers a
  notification only to the listeners of its own database, so the Kit's composition checks fail the Host when the
  listener's connection string is not set, or when a DbContext is stored over another connection string with another
  value.

## Considered Options

- **A per-instance RabbitMQ queue**, rejected by the spec: the broker is optional, and the Kit's durable-queue check
  refuses a non-durable listener.
- **Redis publish and subscribe**, rejected by the spec: no Kit package references a Redis client.
- **A connection list in the process**, as both references keep, rejected: it loses delivery across instances.
- **SignalR, or a second push channel**, rejected by I.6 answer 4. "No second push channel" is read against SignalR
  beside SSE; a database signal between instances is not a channel to a client.

## Consequences

- A Host scales out with no change: every instance hears every push.
- A push costs one statement in its delivery's transaction and one notification per instance; PostgreSQL's
  notification queue (8 GB in a standard installation) fails a committing transaction when it is full.
- Identical pushes on one channel in one transaction arrive once: PostgreSQL folds them.
- The Hosts of one database share the PostgreSQL channel, so two applications on one database name their Channels
  apart.
- A Marten Unit of Work does not push (spec #115, "Out of Scope"): `IUnitOfWorkConnection` is the EF Unit of Work's.

## Invariants worth a permanent test

`RealtimeTests` in `tests/ModularMonolith.Tests` hold each, red once with its behaviour switched off: a push committed
on one instance reaches a client of another, with two instances of one Host on one store under Balanced durability; a
delivery that rolls back pushes nothing; a client receives only its tenant's events and the Channels its endpoint admits
for its Actor; a push of 8000 bytes fails in the handler and one of 7999 is delivered; a killed listener connection
ends in a `gap` and a listener that hears the next push; and, against the sample Host with each feature configured, the
rate limiter counts one permit for a connection, response compression leaves the event stream alone, and status code
pages leave a started connection as it was written.
