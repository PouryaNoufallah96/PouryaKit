namespace Inventory.StockItems.Features.GetStockItem;

internal sealed record StockItemDto(Guid ProductId, int QuantityOnHand);
