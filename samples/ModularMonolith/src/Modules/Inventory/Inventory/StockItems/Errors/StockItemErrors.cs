using PouryaKit.Domain;

namespace Inventory.StockItems.Errors;

internal static class StockItemErrors
{
    public static readonly Error NotFound = Error.NotFound("StockItem.NotFound", "No stock is kept of the product.");
}
