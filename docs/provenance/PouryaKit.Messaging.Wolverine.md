# PouryaKit.Messaging.Wolverine provenance

Adopted implementations in `PouryaKit.Messaging.Wolverine`. Format and rules: [README](README.md).

The delivery of Notifications and Integration Events, one route and durable local queue per subscription and one pre-built handler per message type (`SubscriptionDelivery`, `PouryaKitWolverineExtension`, `Subscriptions`), and the shared delivery step (`Deliver`) are the Kit's own code, adapted from Spike S2's Option C (ticket #5, ADR 0006, tag `spike/s2-independent-delivery`, `spikes/s2-independent-delivery/S2.Kit/OptionC/RoutedQueues.cs` and `S2.Kit/KitMessaging.cs`), so they have no record here. They differ from the Spike in that each delivery runs as a Kit command through the dispatcher, in a scope begun with the context from the headers and the subscribing Module, with a Receipt recorded first; unknown subscriptions raise `UnknownSubscriptionException`; failures are retried with the stored `ScheduleRetry` (ADR 0006, "Owner decisions", 1); a subscription's name is kept lower-cased, as its queue and its Receipts know it; the names of the local queues Wolverine 6.46.0 creates for itself (`agents`, `default`, `durable`, `replies`, `scheduled`, `system`) are refused; and the Host does not start unless an Outbox writes its Notifications and Integration Events (`INotificationOutbox`) and every subscription's queue, as Wolverine built it, is a durable application queue (`StartupCheck`). The `Publish<T>(wireName)` and `Subscribe<T, THandler>(subscriptionName)` declarations, the Integration Event publisher and the process-wide wire names of Integration Events (`WireNaming`) follow the Spike's `KitMessaging` registration (ticket #16); the declarations take the Module explicitly, as `AddPouryaKitNotificationHandlers` does.

## Package dependencies

Checked on nuget.org on 2026-10-05 (ticket #15); licence expressions from each package's nuspec. None is reciprocal or commercial (ADR 0002).

- `WolverineFx` 6.46.0, `MIT`, as ADR 0004 pinned it. Its restored closure on `net10.0` is 44 packages: 42 `MIT`, `NewId` 4.0.1 `Apache-2.0` and `Polly.Core` 8.6.5 `BSD-3-Clause`. It holds no EF Core, no Npgsql, no Weasel, no Wolverine message store and no Roslyn package (story 73).

## Wolverine policy block

- **Kit files:** `src/PouryaKit.Messaging.Wolverine/WolverineHostApplicationBuilderExtensions.cs`, `src/PouryaKit.Messaging.Wolverine.EFCore/WolverineEfCoreHostApplicationBuilderExtensions.cs`, `src/PouryaKit.Messaging.Wolverine.EFCore/PouryaKitMessageStoreOptions.cs`
- **Upstream:** https://github.com/meysamhadeli/woo
- **Commit:** 149a3cf168496c3bec85945f83ad222e43720e4d
- **Upstream paths:** `src/Wolverine/Extensions.cs`
- **Licence:** MIT
- **Adoption:** pattern
- **Ticket:** #15
- **Intentional differences:**
  - Kept: a PostgreSQL message store, the EF Core transaction integration, durable local queues, and five minutes as the default inbox stale time, now a Kit setting documented as longer than the longest delivery (ADR 0004).
  - No RabbitMQ or other transport; no durable inbox or outbox policy on external endpoints, which the Kit does not have yet.
  - No conventional handler discovery and no conventional local routing: every route is one subscription's explicit queue, named after it (plan section B.2, "explicit queue naming").
  - The stored retry `ScheduleRetry`, then the dead-letter queue, instead of the in-memory `RetryWithCooldown`, which `InboxStaleTime` can make run a handler again after it succeeded (ADR 0006).
  - No outbox stale time: the Kit's Notifications go to local queues and are stored as incoming envelopes (ADR 0004).
  - `UnknownMessageBehavior.DeadLetterQueue`, so a stored delivery whose type lost every subscription is never treated as handled (ADR 0006).
