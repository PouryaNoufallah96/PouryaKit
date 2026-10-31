using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using PouryaKit.Web;

namespace Catalog.Products.Features.AskAboutProducts;

// The sample's agent over HTTP, as the caller and in the caller's tenant. Each request is the next turn of the caller's
// conversation, so a repeat is another turn: it carries no Idempotency-Key.
internal sealed class AskAboutProductsEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapPost(
            "/products/ask",
            async (
                AskAboutProductsBody body,
                OperationContext context,
                IGatewayProcessor<CatalogModule> catalog,
                CancellationToken cancellationToken
            ) =>
                (
                    await catalog.SendAsync(
                        new AskAboutProductsCommand(body.Question),
                        context,
                        cancellationToken
                    )
                ).ToHttpResult()
        );
}

internal sealed record AskAboutProductsBody(string Question);
