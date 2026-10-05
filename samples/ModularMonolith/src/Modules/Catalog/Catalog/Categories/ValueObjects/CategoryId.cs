using PouryaKit.Domain;

namespace Catalog.Categories.ValueObjects;

internal sealed record CategoryId(Guid Value) : TypedId<Guid>(Value);
