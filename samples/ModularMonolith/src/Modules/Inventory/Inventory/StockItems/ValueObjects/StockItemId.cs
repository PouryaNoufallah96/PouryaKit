using PouryaKit.Domain;

namespace Inventory.StockItems.ValueObjects;

internal sealed record StockItemId(Guid Value) : TypedId<Guid>(Value);
