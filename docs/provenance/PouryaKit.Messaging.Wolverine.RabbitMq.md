# PouryaKit.Messaging.Wolverine.RabbitMq provenance

Adopted implementations in `PouryaKit.Messaging.Wolverine.RabbitMq`. Format and rules: [README](README.md).

The package has no adopted implementation. Its topology, one fanout exchange per wire name routed from the publication and one durable queue per Host fanned out to the subscriptions' local queues, is the decision of [ADR 0006's #50 amendment](../adr/0006-independent-delivery-through-a-routed-queue-per-subscription.md#amendment-2026-10-07-ticket-50-rabbitmq-topology), taken from the Kit's own Spike, and is written with Wolverine's standard RabbitMQ configuration (`UseRabbitMq`, `AutoProvision`, `DisableDeadLetterQueueing`, `ToRabbitExchange`, `BindExchange`, `ListenToRabbitQueue(...).UseDurableInbox()`). Plan I.3's RabbitMQ row cites `woo@149a3cf src/Wolverine/Extensions.cs:64-88` for the connection and auto-provisioning and `CloudNativeKit@e81d65e src/CloudNativeKit.Integration.Wolverine/WolverineMessageTopologyExtensions.cs:35-75` for an exchange per message and a durable inbox on each listener; both shaped the design only, no code was taken, and the Kit binds one queue per Host instead of one per listener and keeps no static registry. The fan-out in `src/PouryaKit.Messaging.Wolverine/SubscriptionDelivery.cs`, `Receive` and `ReceiveSnapshot`, `IWireNames` and the broker listener check in `src/PouryaKit.Messaging.Wolverine/StartupCheck.cs` are new Kit code too (ticket #51).

## Package dependencies

Checked on nuget.org on 2026-10-07 (ticket #51); licence expressions from each package's restored nuspec. None is reciprocal-only or commercial (ADR 0002), and `LicenceTests` checks the whole closure.

- `WolverineFx.RabbitMQ` 6.46.0, `MIT`, the latest stable version and the release of the Kit's `WolverineFx`, for `net10.0`. Its closure adds:
  - `RabbitMQ.Client` 7.2.2, `Apache-2.0 OR MPL-2.0`: the Kit takes it under `Apache-2.0`, the branch `LicenceTests` accepts;
  - `WolverineFx.Newtonsoft` 6.46.0 and `Newtonsoft.Json` 13.0.3, both `MIT`. The Kit does not serialize with them: envelopes cross the broker in the Kit's System.Text.Json representation, which `RabbitMqDeliveryTests.A_typed_id_crosses_the_broker_as_its_bare_value_and_is_read_back_as_the_typed_id` checks in the stored broker envelope;
  - `System.Threading.RateLimiting` 8.0.0, `MIT`, from `RabbitMQ.Client`.

`PouryaKit.Messaging.Wolverine.RabbitMq` also references `PouryaKit.Messaging.Wolverine`, as plan section C lists since ticket #51; `PackageGraphTests` keeps EF Core, Npgsql and the message-store packages out of its closure. Its private MinVer build reference is recorded in [shared private build tools](README.md#shared-private-build-tools).
