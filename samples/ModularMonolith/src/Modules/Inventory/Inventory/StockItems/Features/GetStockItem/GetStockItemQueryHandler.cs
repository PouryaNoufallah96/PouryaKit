using Dapper;
using Inventory.StockItems.Errors;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence.Dapper;

namespace Inventory.StockItems.Features.GetStockItem;

// Inventory is not tenant-scoped, so nothing filters its reads: the SQL compares the tenant the delivery stored with the
// one the lease bound, the Operation's. A tenantless Operation's lease binds the empty string, which no stored tenant
// equals, and a stock item opened with no tenant equals none, so neither is ever answered.
internal sealed class GetStockItemQueryHandler(IReadConnectionFactory connections) : QueryHandler<GetStockItemQuery, StockItemDto>
{
    public override async Task<Result<StockItemDto>> Handle(GetStockItemQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var stockItem = await connection.QuerySingleOrDefaultAsync<StockItemDto>(
            new CommandDefinition(
                "select product_id as productid, quantity_on_hand as quantityonhand from inventory.stock_items where product_id = @ProductId and tenant = current_setting('pouryakit.tenant')",
                new { query.ProductId },
                cancellationToken: cancellationToken
            )
        );
        return stockItem is null ? StockItemErrors.NotFound : stockItem;
    }
}
