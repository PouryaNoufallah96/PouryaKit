namespace SagaSpike.Contracts;

// Orders publishes these. None carries a tenant: the tenant rides the Operation Context headers, as for every Integration
// Event. PayWithinSeconds exists so a test can choose how soon the saga's timeout fires.

/// <summary>An order was placed; it is paid in <paramref name="Installments"/> payments.</summary>
public sealed record OrderPlacedV1(Guid OrderId, int Installments, int PayWithinSeconds);

/// <summary>One payment of an order was received.</summary>
public sealed record PaymentReceivedV1(Guid OrderId);

/// <summary>The second contract Orders publishes for the same payment, under the same Domain Event identity.</summary>
public sealed record PaymentReceivedV2(Guid OrderId, string Method);

// Shipping's saga emits these; Billing, a tenant-scoped Module, subscribes to them like any Integration Event.

/// <summary>An order was paid in full and is fulfilled.</summary>
public sealed record OrderFulfilledV1(Guid OrderId, int Payments);

/// <summary>An order was not paid in time and is cancelled.</summary>
public sealed record OrderCancelledV1(Guid OrderId, int Payments);
