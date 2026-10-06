namespace Catalog.Contracts.Products.Events.V1;

/// <summary>
/// The current state of one product, which supersedes every earlier Snapshot of it: <paramref name="Version"/> grows
/// with every change Catalog commits, so apply it only when it is newer than the one applied. When
/// <paramref name="IsRemoved"/> is true it is the product's Tombstone: the product has left the catalog. Catalog publishes
/// it under the wire name <c>catalog.product-snapshot.v1</c>; a change other Modules could not read is a new version,
/// not an edit of this one.
/// </summary>
public sealed record ProductSnapshotV1(Guid ProductId, string Name, bool IsRemoved, long Version);
