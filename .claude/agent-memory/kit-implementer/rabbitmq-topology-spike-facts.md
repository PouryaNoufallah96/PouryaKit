---
name: rabbitmq-topology-spike-facts
description: Spike #50 facts - WolverineFx.RabbitMQ 6.46 identity, dead-letter and recovery behaviour, two-Host Testcontainers harness traps (vhosts, connection exhaustion, VSTest fallback)
metadata:
  type: project
---

Found in Spike #50 (2026-10-07), WolverineFx.RabbitMQ 6.46.0, RabbitMQ 4.3.6. The record is the ticket #50 amendment
of ADR 0006; check it before relying on this.

- **One envelope id on two queues:** a fanout exchange copies the Wolverine envelope id to every bound queue. Under
  `MessageIdentity.IdOnly`, the second Inbox insert raises `DuplicateIncomingEnvelopeException`, and that delivery is
  acknowledged and dropped silently. A queue per subscription needs `IdAndDestination`, which changes the keys of the
  incoming and dead-letter tables. The Kit chose one broker queue per Host, fanned out to local queues (keeps `IdOnly`).
- **Dead letters:** without `DisableDeadLetterQueueing()`, Wolverine declares a native `wolverine-dead-letter-queue`,
  and exhausted deliveries leave Wolverine's storage.
- **Addresses:** a queue listener's address is `rabbitmq://queue/{name}`. Its `Uri.Host` is `queue`, so a lookup by
  `Destination.Host` breaks. `ScheduleRetry` and recovery of such rows work through the listener circuit.
- **Durable receiver settles first:** the broker delivery is acknowledged once the Inbox insert commits, so the broker
  queue shows 0 while a retry waits in the store.
- **A mutation that did not fail:** under the fan-out topology, replacing the broker listener's `UseDurableInbox()` with
  `BufferedInMemory()` did not fail the restart test. The waiting retry sits in the durable local queue.
- **Harness: a vhost per test.** `container.ExecAsync(["rabbitmqctl","add_vhost",v])` plus `set_permissions -p v
  <user> ".*" ".*" ".*"`, then the AMQP URI path `/v`. The default Testcontainers user is `rabbitmq`.
- **Harness: connection exhaustion.** About 8 Host pairs on fresh databases exhausted PostgreSQL's 100 connections
  (53300). Set `-c max_connections=1000`, and register a data source with a factory: `AddSingleton(instance)` is never
  disposed.
- **Harness: the VSTest fallback.** A `dotnet test --project` launched with a working directory that has no
  `global.json` above it runs VSTest and fails with MSB1001. Run mutation scripts with `cwd` inside the repository.
- **Harness: builder constructors.** Testcontainers 4.15 marks parameterless builders obsolete; use
  `new RabbitMqBuilder(image)`.
- **Review lesson.** I first used "two Hosts with the same subscription name split messages under A" as a reason to
  reject A. It failed the fair-configuration rule: with A's queue named `{ServiceName}.{subscription}`, each Host got
  every message, and B has the same risk through a shared `ServiceName`. Before a naming or configuration choice
  counts against an option, try that option's best variant of it. In an ADR, write "Host", not "service" or
  "consumer", and "subscription", not "subscriber" (GLOSSARY synonyms).
- RabbitMQ.Client 7.2.2 is licensed `Apache-2.0 OR MPL-2.0`; the Kit takes it under Apache-2.0. WolverineFx.RabbitMQ
  pulls WolverineFx.Newtonsoft and Newtonsoft.Json, both MIT.

**Why:** each one either decided the topology or cost a run.
**How to apply:** for #51 (the RabbitMQ package and its two-Host Gateway tests). Related: [[wolverine-spike-gotchas]],
[[messaging-delivery-facts]], [[dead-letter-and-stale-time-facts]].
