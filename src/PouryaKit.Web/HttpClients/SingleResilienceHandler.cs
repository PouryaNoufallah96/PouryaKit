using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;

namespace PouryaKit.Web;

// Refuses a client whose handler chain, as every configuration callback left it, holds two resilience handlers: each would
// retry the other's attempts, as when the defaults of every client (ConfigureHttpClientDefaults) add one and the client
// adds its own. Registration history cannot tell, since RemoveAllResilienceHandlers removes handlers only while the chain
// is built. HttpClientFactory builds a client's chain once, when it first creates the client, and keeps the exception, so
// every creation of that client throws it.
internal sealed class SingleResilienceHandler : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) =>
        builder =>
        {
            next(builder);
            var handlers = builder.AdditionalHandlers.OfType<ResilienceHandler>().Count();
            if (handlers > 1)
            {
                throw new InvalidOperationException(
                    $"The HTTP client '{builder.Name}' has {handlers} resilience handlers, so each would retry the other's attempts, as when the defaults of every client add one and the client adds its own. Call RemoveAllResilienceHandlers() on the client before AddPouryaKitResilience(), or remove the client's own."
                );
            }
        };
}
