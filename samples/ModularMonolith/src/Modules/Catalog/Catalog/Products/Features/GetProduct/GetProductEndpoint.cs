using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using PouryaKit.Web;

namespace Catalog.Products.Features.GetProduct;

// No :guid route constraint: an id that does not parse is the caller's mistake (400), not a missing route (404).
internal sealed class GetProductEndpoint : IEndpoint
{
    /// <summary>The endpoint's name, which a link to a product names.</summary>
    public const string Name = "GetProduct";

    public void Map(IEndpointRouteBuilder routes) =>
        routes
            .MapGet(
                "/products/{productId}",
                async (
                    Guid productId,
                    OperationContext context,
                    IGatewayProcessor<CatalogModule> catalog,
                    CancellationToken cancellationToken
                ) =>
                    (
                        await catalog.QueryAsync(
                            new GetProductQuery(productId),
                            context,
                            cancellationToken
                        )
                    ).ToHttpResult()
            )
            .WithName(Name);
}
