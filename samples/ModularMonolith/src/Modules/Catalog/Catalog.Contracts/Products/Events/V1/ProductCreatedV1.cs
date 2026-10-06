namespace Catalog.Contracts.Products.Events.V1;

/// <summary>
/// A product was added to the catalog. <paramref name="Id"/> and <paramref name="OccurredOn"/> are the identity and the
/// time of the Domain Event it follows from, the same in every delivery. Catalog publishes it under the wire name
/// <c>catalog.product-created.v1</c>; a change other Modules could not read is a new version, not an edit of this one.
/// </summary>
public sealed record ProductCreatedV1(Guid Id, DateTimeOffset OccurredOn, Guid ProductId, string Name);
