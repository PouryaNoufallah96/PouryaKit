using PouryaKit.Cqrs;

namespace Inventory.StockItems.Features.ReconcileStock;

/// <summary>One occurrence of Inventory's recurring stock reconciliation, at <paramref name="Occurrence"/>.</summary>
internal sealed record ReconcileStockCommand(DateTimeOffset Occurrence) : Command;
