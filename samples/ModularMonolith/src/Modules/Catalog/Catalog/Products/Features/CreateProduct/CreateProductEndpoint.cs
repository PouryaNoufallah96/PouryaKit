using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using PouryaKit.Web;

namespace Catalog.Products.Features.CreateProduct;

internal sealed class CreateProductEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapPost(
            "/products",
            async (
                CreateProductBody body,
                OperationContext context,
                IGatewayProcessor<CatalogModule> catalog,
                CancellationToken cancellationToken
            ) =>
                (
                    await catalog.SendAsync(
                        new CreateProductCommand(body.ProductId, body.CategoryId, body.Name),
                        context,
                        cancellationToken
                    )
                ).ToHttpResult(id => TypedResults.Created($"/catalog/products/{id}", new { id }))
        );
}

internal sealed record CreateProductBody(Guid ProductId, Guid CategoryId, string Name);
