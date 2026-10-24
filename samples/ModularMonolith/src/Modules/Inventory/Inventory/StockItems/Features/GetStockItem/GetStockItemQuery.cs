using PouryaKit.Cqrs;

namespace Inventory.StockItems.Features.GetStockItem;

internal sealed record GetStockItemQuery(Guid ProductId) : Query<StockItemDto>;
