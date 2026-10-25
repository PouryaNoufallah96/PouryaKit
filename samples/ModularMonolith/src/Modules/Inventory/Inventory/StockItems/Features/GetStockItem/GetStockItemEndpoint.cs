using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using PouryaKit.Web;

namespace Inventory.StockItems.Features.GetStockItem;

// No :guid route constraint: an id that does not parse is the caller's mistake (400), not a missing route (404).
internal sealed class GetStockItemEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapGet(
            "/stock-items/{productId}",
            async (Guid productId, OperationContext context, IGatewayProcessor<InventoryModule> inventory, CancellationToken cancellationToken) =>
                (await inventory.QueryAsync(new GetStockItemQuery(productId), context, cancellationToken)).ToHttpResult()
        );
}
