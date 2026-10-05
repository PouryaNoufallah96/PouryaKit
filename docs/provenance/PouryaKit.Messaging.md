# PouryaKit.Messaging provenance

Adopted implementations in `PouryaKit.Messaging`. Format and rules: [README](README.md).

`INotificationHandler<TDomainEvent>`, the wire and subscription names (`MessageNames`, `WireNameAttribute`, `SubscriptionNameAttribute`) and the Operation Context header names (`OperationContextHeaders`) are new Kit code, so they have no record here. The handler shape and the explicit names come from Spike S2 (ticket #5, ADR 0006, tag `spike/s2-independent-delivery`), which is the Kit's own code; the names are derived by convention with an explicit, stable override, as the owner decided on 2026-10-05 (ADR 0006, "Owner decisions", 2), ticket #15.

`IIntegrationEventPublisher`, `IIntegrationEventHandler<TIntegrationEvent>` and the identity header name (`IntegrationEventHeaders`) are new Kit code too, from the publishing and receiving shapes of plan section D and Spike S2's handler interfaces, ticket #16. An Integration Event is a plain record of the publishing Module's Contracts, so its wire name is declared where it is published, not with an attribute on the type.

## Package dependency

None. `PouryaKit.Messaging` references `PouryaKit.Domain` only, as plan section C lists, so a Notification Handler written against it names no transport and no database type.
