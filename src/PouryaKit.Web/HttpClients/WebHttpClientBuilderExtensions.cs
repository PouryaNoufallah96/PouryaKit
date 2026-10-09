using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Polly;
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
        /// <c>RemoveAllResilienceHandlers()</c> first. In a Host that calls it, a client whose handler chain, once built,
        /// holds two resilience handlers throws <see cref="InvalidOperationException"/> whenever it is created, naming that
        /// call, instead of retrying each attempt of the other. The replay policy of the last call for a client is the
        /// one its pipeline uses, so a client registered again after <c>RemoveAllResilienceHandlers()</c> keeps no earlier
        /// policy.
        /// The client registered with an empty name is refused: its pipeline would be the one the defaults of every
        /// client (<c>ConfigureHttpClientDefaults</c>) register, with their options and replay policy.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="replay"/> is not a defined value.</exception>
        /// <exception cref="ArgumentException">The client was registered with an empty name.</exception>
        public IHttpStandardResiliencePipelineBuilder AddPouryaKitResilience(HttpReplay replay = HttpReplay.SafeMethods)
        {
            ArgumentNullException.ThrowIfNull(builder);
            if (!Enum.IsDefined(replay))
            {
                throw new ArgumentOutOfRangeException(nameof(replay), replay, "HttpReplay has no such value.");
            }

            // The standard pipeline is named after the client: "-standard" for the defaults' builder, whose name is null,
            // and for the client named "" alike. One options name holds one replay policy, so they cannot be kept apart.
            if (builder.Name is "")
            {
                throw new ArgumentException(
                    "The client with the empty name would share the resilience pipeline the defaults of every client register (ConfigureHttpClientDefaults), and its replay policy. Give the client a name.",
                    nameof(builder)
                );
            }

            var pipeline = builder.AddStandardResilienceHandler();
            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, SingleResilienceHandler>());
            var policies = HttpReplayPolicies.In(builder.Services);
            if (policies.Set(pipeline.PipelineName, replay))
            {
                // After every Configure, so a retry condition the application sets is narrowed too, not replaced. It
                // reads the policy when the options are built, so the last call for the client decides it.
                builder.Services.PostConfigure<HttpStandardResilienceOptions>(
                    pipeline.PipelineName,
                    options =>
                    {
                        if (policies.For(pipeline.PipelineName) is HttpReplay.SafeMethods)
                        {
                            RetrySafeMethodsOnly(options);
                        }
                    }
                );
            }

            return pipeline;
        }
    }

    private static readonly HttpMethod[] SafeHttpMethods = [HttpMethod.Get, HttpMethod.Head, HttpMethod.Options, HttpMethod.Trace];

    // An allow-list: a method the Kit does not know to be safe, such as WebDAV's PROPPATCH, is a write and is sent once.
    // A request the pipeline cannot name is not retried either.
    private static void RetrySafeMethodsOnly(HttpStandardResilienceOptions options)
    {
        var shouldHandle = options.Retry.ShouldHandle;
        options.Retry.ShouldHandle = async arguments =>
            arguments.Context.GetRequestMessage() is { } request
            && SafeHttpMethods.Contains(request.Method)
            && await shouldHandle(arguments);
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
