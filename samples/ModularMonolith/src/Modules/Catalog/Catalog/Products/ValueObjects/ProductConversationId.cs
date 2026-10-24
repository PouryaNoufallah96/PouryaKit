using PouryaKit.Domain;

namespace Catalog.Products.ValueObjects;

internal sealed record ProductConversationId(Guid Value) : TypedId<Guid>(Value);
