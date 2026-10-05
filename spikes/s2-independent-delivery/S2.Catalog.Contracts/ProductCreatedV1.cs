namespace S2.Catalog.Contracts;

/// <summary>Integration Event published by Catalog. <paramref name="EventId"/> is the Domain Event's identity.</summary>
public sealed record ProductCreatedV1(Guid EventId, Guid ProductId, string Name);
