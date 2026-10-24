using Microsoft.AspNetCore.Routing;

namespace PouryaKit.Web;

/// <summary>
/// The HTTP endpoint of one Feature: a small class beside the Feature, internal to its Module, that maps its route and
/// sends the Feature's command or query through the Module's Gateway; an SSE endpoint that maps
/// <c>MapPouryaKitRealtime</c> of <c>PouryaKit.Realtime</c> sends nothing and serves the request's own server-sent events.
/// It is found in its Module's assembly and mapped under the Module's route group, which requires an authenticated
/// caller; there is no central route list. It needs a parameterless constructor.
/// </summary>
/// <example>
/// <code>
/// internal sealed class GetProductEndpoint : IEndpoint
/// {
///     public void Map(IEndpointRouteBuilder routes) =>
///         routes.MapGet(
///             "/products/{productId}",
///             async (Guid productId, OperationContext context, IGatewayProcessor&lt;CatalogModule&gt; catalog, CancellationToken ct) =>
///                 (await catalog.QueryAsync(new GetProductQuery(productId), context, ct)).ToHttpResult()
///         );
/// }
/// </code>
/// </example>
public interface IEndpoint
{
    /// <summary>Maps the Feature's route on <paramref name="routes"/>, its Module's route group.</summary>
    void Map(IEndpointRouteBuilder routes);
}
