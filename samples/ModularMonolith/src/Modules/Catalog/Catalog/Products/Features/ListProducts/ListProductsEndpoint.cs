using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using PouryaKit.Web;

namespace Catalog.Products.Features.ListProducts;

// Authenticated, as every Catalog endpoint is: Catalog is tenant-scoped, and a tenant comes only from a verified claim,
// so an anonymous visitor would have none.
internal sealed class ListProductsEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapGet(
            "/products",
            async (
                HttpPageRequest page,
                OperationContext context,
                IGatewayProcessor<CatalogModule> catalog,
                CancellationToken cancellationToken
            ) => (await catalog.QueryAsync(new ListProductsQuery(page.Request), context, cancellationToken)).ToHttpResult()
        );
}
