using PouryaKit.Cqrs;

namespace PouryaKit.Web;

// Writes the Operation Context a request was given with WithOperationContext as the Kit's headers, each time the
// request passes, so a request the resilience handler sends again carries the same context. HttpClientFactory builds
// its handlers in a scope of its own, not the calling Operation's, so the context travels on the request, never through
// the handler's services. It reads no incoming request and copies no header from one: the caller's bearer token is never
// forwarded, and a client that must authenticate to the peer adds its own credentials.
internal sealed class OperationContextHttpHandler : DelegatingHandler
{
    internal static readonly HttpRequestOptionsKey<OperationContext> Key = new("PouryaKit.OperationContext");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        WriteContext(request);
        return base.SendAsync(request, cancellationToken);
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        WriteContext(request);
        return base.Send(request, cancellationToken);
    }

    private static void WriteContext(HttpRequestMessage request) =>
        OperationContextHttpHeaders.Write(
            request.Headers,
            request.Options.TryGetValue(Key, out var context)
                ? context
                : throw new InvalidOperationException(
                    $"{request.Method} {request.RequestUri} has no Operation Context, and its client sends one with every request. Give it the calling Operation's context with WithOperationContext(context)."
                )
        );
}
