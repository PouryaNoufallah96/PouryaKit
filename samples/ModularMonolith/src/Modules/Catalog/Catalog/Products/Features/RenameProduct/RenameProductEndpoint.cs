using Asp.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using PouryaKit.Web;

namespace Catalog.Products.Features.RenameProduct;

// Renaming arrived in v2; v1 callers cannot rename.
internal sealed class RenameProductEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes
            .MapPut(
                "/products/{productId}/name",
                async (
                    Guid productId,
                    RenameProductBody body,
                    OperationContext context,
                    IGatewayProcessor<CatalogModule> catalog,
                    CancellationToken cancellationToken
                ) =>
                    (
                        await catalog.SendAsync(
                            new RenameProductCommand(productId, body.Name),
                            context,
                            cancellationToken
                        )
                    ).ToHttpResult()
            )
            .MapToApiVersion(new ApiVersion(2, 0));
}

internal sealed record RenameProductBody(string Name);
