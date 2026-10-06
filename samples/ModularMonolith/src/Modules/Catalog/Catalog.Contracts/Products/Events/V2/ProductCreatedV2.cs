namespace Catalog.Contracts.Products.Events.V2;

/// <summary>
/// A product was added to the catalog, in its category <paramref name="CategoryId"/>: the second version of
/// <c>ProductCreatedV1</c>, a type of its own under the wire name <c>catalog.product-created.v2</c>. Catalog publishes
/// both versions for every product while subscribers move from v1 to v2, and each subscription receives only the
/// version it subscribed to. <paramref name="Id"/> and <paramref name="OccurredOn"/> are the identity and the time of
/// the Domain Event it follows from, the same as v1's.
/// </summary>
public sealed record ProductCreatedV2(Guid Id, DateTimeOffset OccurredOn, Guid ProductId, string Name, Guid CategoryId);
