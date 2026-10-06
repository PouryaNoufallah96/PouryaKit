using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using PouryaKit.Web;

namespace Catalog.Categories.Features.CreateCategory;

// Catalog has no Feature that reads one category, so there is no location to give: the new id is the body.
internal sealed class CreateCategoryEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapPost(
            "/categories",
            async (
                CreateCategoryBody body,
                OperationContext context,
                IGatewayProcessor<CatalogModule> catalog,
                CancellationToken cancellationToken
            ) =>
                (
                    await catalog.SendAsync(
                        new CreateCategoryCommand(body.CategoryId, body.Name),
                        context,
                        cancellationToken
                    )
                ).ToHttpResult()
        );
}

internal sealed record CreateCategoryBody(Guid CategoryId, string Name);
