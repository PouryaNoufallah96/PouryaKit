using PouryaKit.Domain;

namespace Catalog.Products.ValueObjects;

internal sealed record ProductId(Guid Value) : TypedId<Guid>(Value);
