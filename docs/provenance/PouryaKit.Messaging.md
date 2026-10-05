# PouryaKit.Messaging provenance

Adopted implementations in `PouryaKit.Messaging`. Format and rules: [README](README.md).

`INotificationHandler<TDomainEvent>`, the wire and subscription names (`MessageNames`, `WireNameAttribute`, `SubscriptionNameAttribute`) and the Operation Context header names (`OperationContextHeaders`) are new Kit code, so they have no record here. The handler shape and the explicit names come from Spike S2 (ticket #5, ADR 0006, tag `spike/s2-independent-delivery`), which is the Kit's own code; the names are derived by convention with an explicit, stable override, as the owner decided on 2026-10-05 (ADR 0006, "Owner decisions", 2), ticket #15.

## Package dependency

None. `PouryaKit.Messaging` references `PouryaKit.Domain` only, as plan section C lists, so a Notification Handler written against it names no transport and no database type.
