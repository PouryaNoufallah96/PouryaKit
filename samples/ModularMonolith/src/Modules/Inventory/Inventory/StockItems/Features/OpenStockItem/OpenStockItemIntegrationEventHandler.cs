using Catalog.Contracts.Products.Events.V1;
using Inventory.StockItems.Entities;
using Inventory.StockItems.ValueObjects;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Persistence;

namespace Inventory.StockItems.Features.OpenStockItem;

// Starts keeping stock of every product Catalog creates. Only the effect: the Kit records the Receipt, commits and
// acknowledges, so a redelivered or concurrently duplicated ProductCreatedV1 opens one stock item.
internal sealed class OpenStockItemIntegrationEventHandler(IRepository<StockItem, StockItemId> stockItems)
    : IIntegrationEventHandler<ProductCreatedV1>
{
    public async Task<Result> Handle(ProductCreatedV1 integrationEvent, CancellationToken cancellationToken)
    {
        await stockItems.AddAsync(StockItem.Open(integrationEvent.ProductId, integrationEvent.OccurredOn), cancellationToken);
        return Result.Success();
    }
}
