using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ModularMonolith.Host;
using PouryaKit.Cqrs;
using PouryaKit.Web;

namespace ModularMonolith.Tests.Stub;

// Calls the absolute url it is given through the Host's downstream client, with the request's Operation Context, and
// answers that context, so a test compares what the downstream server received with the Operation that sent it.
internal sealed class RelayEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapGet(
            "/relay",
            async (
                string url,
                OperationContext context,
                IHttpClientFactory clients,
                CancellationToken cancellationToken
            ) =>
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    url
                ).WithOperationContext(context);
                using var response = await clients
                    .CreateClient(ModularMonolithHost.DownstreamClient)
                    .SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                return TypedResults.Ok(context);
            }
        );
}
