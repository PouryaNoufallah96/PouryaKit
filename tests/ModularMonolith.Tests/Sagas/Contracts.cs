namespace ModularMonolith.Tests.Sagas;

// The Integration Events of the Saga tests. None carries a tenant: the tenant rides the Operation Context headers, as for
// every Integration Event. They are public, as a Module's Contracts are, since the code Wolverine generates for a Saga's
// step names its input.

/// <summary>An order was placed; it is paid in <paramref name="Installments"/> payments within <paramref name="PayWithinSeconds"/>.</summary>
public sealed record OrderPlacedV1(Guid OrderId, int Installments, int PayWithinSeconds);

/// <summary>One payment of an order was received.</summary>
public sealed record PaymentReceivedV1(Guid OrderId);

/// <summary>The second contract Orders publishes for the same payment, under the same Domain Event identity.</summary>
public sealed record PaymentReceivedV2(Guid OrderId, string Method);

/// <summary>A reference was opened; its business id is any text.</summary>
public sealed record ReferenceOpenedV1(string Reference);

/// <summary>An order was paid in full and is fulfilled.</summary>
public sealed record OrderFulfilledV1(Guid OrderId, int Payments);

/// <summary>An order was not paid in time and is cancelled.</summary>
public sealed record OrderCancelledV1(Guid OrderId, int Payments);
