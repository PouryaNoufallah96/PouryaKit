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
                CreateProductRequest request,
                OperationContext context,
                IGatewayProcessor<CatalogModule> catalog,
                CancellationToken cancellationToken
            ) =>
                (
                    await catalog.SendAsync(
                        new CreateProductCommand(request.ProductId, request.CategoryId, request.Name),
                        context,
                        cancellationToken
                    )
                ).ToHttpResult(id => TypedResults.Created($"/catalog/products/{id}", new { id }))
        );
}

internal sealed record CreateProductRequest(Guid ProductId, Guid CategoryId, string Name);
