using PouryaKit.Cqrs;

namespace Inventory.StockCounts.Features.StartStockCount;

/// <summary>Starts a stock count, which receives the next number of its year's sequence.</summary>
internal sealed record StartStockCountCommand : Command<long>;
