using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using PouryaKit.Cqrs;
using PouryaKit.Web;

namespace Catalog.Categories.Features.CreateCategory;

// Catalog has no Feature that reads one category, so there is no location to give: the new id is the body.
internal sealed class CreateCategoryEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapPost(
            "/categories",
            async (CreateCategoryBody body, IDispatcher dispatcher, CancellationToken cancellationToken) =>
                (await dispatcher.SendAsync(new CreateCategoryCommand(body.CategoryId, body.Name), cancellationToken)).ToHttpResult()
        );
}

internal sealed record CreateCategoryBody(Guid CategoryId, string Name);
