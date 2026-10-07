using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using PouryaKit.Cqrs;
using PouryaKit.Web;

namespace Catalog.Products.Features.ListProducts;

// The product list is the storefront's: anyone may read it, and an anonymous visitor's Operation runs as the
// storefront.
internal sealed class ListProductsEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes
            .MapGet(
                "/products",
                async (HttpPageRequest page, IDispatcher dispatcher, CancellationToken cancellationToken) =>
                    (await dispatcher.QueryAsync(new ListProductsQuery(page.Request), cancellationToken)).ToHttpResult()
            )
            .AllowAnonymousAs(Actor.System("catalog.storefront"));
}
