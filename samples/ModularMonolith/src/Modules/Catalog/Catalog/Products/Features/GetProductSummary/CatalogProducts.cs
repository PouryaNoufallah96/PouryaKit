using Catalog.Contracts.Products;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;

namespace Catalog.Products.Features.GetProductSummary;

// Registered scoped, so it is resolved in the asking Operation's scope and receives that Operation's context. It runs
// in another Module's scope, so it uses nothing of Catalog's but its Gateway.
internal sealed class CatalogProducts(IGatewayProcessor<CatalogModule> catalog, OperationContext asking) : ICatalogProducts
{
    public async Task<ProductSummary?> FindProductAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        var found = await catalog.QueryAsync(new GetProductSummaryQuery(productId), asking.Continue(), cancellationToken);
        if (found.IsSuccess)
        {
            return found.Value;
        }

        // Only "no such product" is an answer; any other failure is a bug in the question, not a business outcome.
        return found.Errors is [var error] && error == GetProductSummaryQueryHandler.ProductNotFound
            ? null
            : throw new InvalidOperationException(
                $"Catalog could not answer FindProductAsync: {string.Join(", ", found.Errors.Select(e => e.Code))}."
            );
    }
}
