using System.Globalization;
using Catalog.Products.Features.GetProduct;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PouryaKit.Cqrs;
using PouryaKit.Web;

namespace Catalog.Products.Features.CreateProduct;

// The location is the new product's versioned path, in the API version the request selected by URL segment or by
// header, so a caller follows it without naming a version again. Every request carries an Idempotency-Key; a repeat
// answers the first response.
internal sealed class CreateProductEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapPost(
            "/products",
            async (
                CreateProductBody body,
                IDispatcher dispatcher,
                HttpContext httpContext,
                LinkGenerator links,
                CancellationToken cancellationToken
            ) =>
                (
                    await dispatcher.SendAsync(new CreateProductCommand(body.ProductId, body.CategoryId, body.Name), cancellationToken)
                ).ToHttpResult(id =>
                    TypedResults.Created(
                        links.GetPathByName(
                            httpContext,
                            GetProductEndpoint.Name,
                            new
                            {
                                productId = id,
                                version = httpContext.RequestedApiVersion?.ToString("VVV", CultureInfo.InvariantCulture),
                            }
                        ),
                        new { id }
                    )
                )
        )
        .RequireIdempotencyKey();
}

internal sealed record CreateProductBody(Guid ProductId, Guid CategoryId, string Name);
