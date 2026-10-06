using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using PouryaKit.Cqrs;

namespace PouryaKit.Web;

public static class WebHttpClientBuilderExtensions
{
    extension(IHttpClientBuilder builder)
    {
        /// <summary>
        /// Sends the Operation Context every request of this client is given with <c>WithOperationContext</c> as the
        /// Kit's headers: the Actor, the tenant, the correlation, and the calling Operation's <c>OperationId</c> as the
        /// causation. A Kit Host that trusts this one as a peer continues the Operation from them. Nothing of the
        /// incoming request is copied, its bearer token least of all; a request without a context throws before it is
        /// sent. Add it after <see cref="AddPouryaKitResilience"/>, so each attempt passes it.
        /// </summary>
        public IHttpClientBuilder AddPouryaKitOperationContext()
        {
            ArgumentNullException.ThrowIfNull(builder);
            return builder.AddHttpMessageHandler(() => new OperationContextHttpHandler());
        }

        /// <summary>
        /// Adds the standard .NET resilience handler (<c>AddStandardResilienceHandler</c>): attempt and total timeouts, a
        /// circuit breaker and retries of transient failures, which stop when the request is cancelled. With
        /// <see cref="HttpReplay.SafeMethods"/>, the default, a write is never sent twice; only a client whose
        /// downstream API makes every write replay-safe passes <see cref="HttpReplay.AllRequests"/>. Configure limits
        /// on the builder it returns, such as <c>.Configure(o => o.Retry.MaxRetryAttempts = 2)</c>. It needs no Aspire
        /// or ServiceDefaults; a Host whose ServiceDefaults already add the standard handler to every client keeps one
        /// pipeline: it calls this in place of that line, or removes it for the client with
        /// <c>RemoveAllResilienceHandlers()</c> first.
        /// </summary>
        public IHttpStandardResiliencePipelineBuilder AddPouryaKitResilience(HttpReplay replay = HttpReplay.SafeMethods)
        {
            ArgumentNullException.ThrowIfNull(builder);
            var pipeline = builder.AddStandardResilienceHandler();
            if (replay == HttpReplay.SafeMethods)
            {
                // After every Configure, so a retry condition the application sets is narrowed too, not replaced.
                builder.Services.PostConfigure<HttpStandardResilienceOptions>(
                    pipeline.PipelineName,
                    options => options.Retry.DisableForUnsafeHttpMethods()
                );
            }

            return pipeline;
        }
    }

    extension(HttpRequestMessage request)
    {
        /// <summary>
        /// Gives the request the calling Operation's <paramref name="context"/>, which a client with
        /// <c>AddPouryaKitOperationContext</c> sends as headers on every attempt. A typed client that injects
        /// <see cref="OperationContext"/> passes its own.
        /// </summary>
        public HttpRequestMessage WithOperationContext(OperationContext context)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(context);
            request.Options.Set(OperationContextHttpHandler.Key, context);
            return request;
        }
    }
}
