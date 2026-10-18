using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenAI;
using PouryaKit.Cqrs;

namespace PouryaKit.AI;

/// <summary>Composes the AI Capability's chat client on a Host's services.</summary>
public static class AIServiceCollectionExtensions
{
    /// <summary>
    /// The name of the HTTP client the chat client sends through: the defaults the Host gives every client apply to it,
    /// and the Host configures it by this name, as with <c>AddHttpClient(HttpClientName)</c>.
    /// </summary>
    public const string HttpClientName = "PouryaKit.AI";

    // Ollama ignores the key, but the OpenAI client sends one with every request.
    private const string UnusedKey = "unused";

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the Host's one <see cref="IChatClient"/>, a singleton that reaches the provider of
        /// <see cref="PouryaKitAIOptions"/> through the OpenAI client and its chat completions API, over the HTTP client
        /// named <see cref="HttpClientName"/>. The package adds no resilience to that client, and the OpenAI client retries
        /// nothing, so a Host whose defaults give every client one resilience pipeline keeps exactly that one. Each run of
        /// the client, one <c>GetResponseAsync</c> or <c>GetStreamingResponseAsync</c> call, as each agent's run makes, is
        /// given only the tools <see cref="PouryaKitAIOptions.Tools"/> names and the declarations it never invokes, invokes
        /// those the model asks for, a Gateway tool as an Operation, calls the model at most
        /// <see cref="PouryaKitAIOptions.MaximumIterationsPerRun"/> times with tools, and checks each call to the model with
        /// every <see cref="IChatGuardrail"/> the Host registers, none by default; its telemetry carries no message
        /// content. The Host binds the options from its configuration, as with
        /// <c>Configure&lt;PouryaKitAIOptions&gt;(configuration.GetSection("PouryaKit:AI"))</c>, and fails when it starts,
        /// naming the option, when the provider is not an <see cref="AIProvider"/> value, the model is blank, the endpoint
        /// is not an absolute HTTP or HTTPS address or is missing for Azure OpenAI or Ollama, the key is missing for
        /// OpenAI or Azure OpenAI, the iteration cap is below 1 or a tool name is blank. A Host composes exactly one
        /// <see cref="IChatClient"/>: its build fails, through the Kit's composition checks, naming each registration
        /// when it registers another, before or after this call, and naming the contract when it removes this one; it fails
        /// too, naming each, when it registers an <see cref="IChatGuardrail"/> that is not a singleton.
        /// </summary>
        public IServiceCollection AddPouryaKitAI()
        {
            ArgumentNullException.ThrowIfNull(services);
            services
                .AddOptions<PouryaKitAIOptions>()
                .Validate(
                    ai => Enum.IsDefined(ai.Provider),
                    "PouryaKitAIOptions.Provider is not an AIProvider value; set OpenAI, AzureOpenAI or Ollama."
                )
                .Validate(
                    ai => !string.IsNullOrWhiteSpace(ai.Model),
                    "PouryaKitAIOptions.Model names no model; set the model the Host's runs use, or for Azure OpenAI its deployment."
                )
                .Validate(
                    ai => ai.Endpoint is null || (ai.Endpoint.IsAbsoluteUri && (ai.Endpoint.Scheme == Uri.UriSchemeHttp || ai.Endpoint.Scheme == Uri.UriSchemeHttps)),
                    "PouryaKitAIOptions.Endpoint is not an absolute HTTP or HTTPS address."
                )
                .Validate(
                    ai => ai.Endpoint is not null || ai.Provider is not (AIProvider.AzureOpenAI or AIProvider.Ollama),
                    "PouryaKitAIOptions.Endpoint is required for Azure OpenAI, its resource's v1 endpoint, and for Ollama, its OpenAI-compatible API."
                )
                .Validate(
                    ai => !string.IsNullOrWhiteSpace(ai.ApiKey) || ai.Provider is not (AIProvider.OpenAI or AIProvider.AzureOpenAI),
                    "PouryaKitAIOptions.ApiKey is required for OpenAI and Azure OpenAI; set it from a secret store, never in source control."
                )
                .Validate(
                    ai => ai.MaximumIterationsPerRun >= 1,
                    "PouryaKitAIOptions.MaximumIterationsPerRun is below 1; a run calls the model at least once."
                )
                .Validate(
                    ai => !ai.Tools.Any(string.IsNullOrWhiteSpace),
                    "PouryaKitAIOptions.Tools names a blank tool; name each tool a run may be given."
                )
                .ValidateOnStart();
            // The chat client is a singleton that keeps its HttpClient, so connections are renewed by their own lifetime
            // instead of by the factory's handler rotation, as IHttpClientFactory's guidance asks of a long-lived client.
            services
                .AddHttpClient(HttpClientName)
                .UseSocketsHttpHandler((handler, _) => handler.PooledConnectionLifetime = TimeSpan.FromMinutes(2))
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompositionCheck, OneChatClientCheck>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompositionCheck, SingletonGuardrailsCheck>());
            services.AddSingleton<IChatClient>(Create);
            return services;
        }
    }

    // A named method, which the one-client check names. Function invocation sits inside the allow-list, so it never sees
    // a withheld tool, and outside the guardrails, telemetry and logging, which see each call to the model.
    private static IChatClient Create(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<PouryaKitAIOptions>>().Value;
        var openAI = new OpenAIClient(
            new ApiKeyCredential(string.IsNullOrWhiteSpace(options.ApiKey) ? UnusedKey : options.ApiKey),
            new OpenAIClientOptions
            {
                Endpoint = options.Endpoint,
                Transport = new HttpClientPipelineTransport(services.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName)),
                RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            }
        );
        var client = new ChatClientBuilder(openAI.GetChatClient(options.Model).AsIChatClient())
            .Use(inner => new ToolAllowList(inner, options.Tools))
            .UseFunctionInvocation(configure: invocation => invocation.MaximumIterationsPerRequest = options.MaximumIterationsPerRun);
        IChatGuardrail[] guardrails = [.. services.GetServices<IChatGuardrail>()];
        if (guardrails is not [])
        {
            client.Use(inner => new ChatGuardrails(inner, guardrails));
        }

        return client
            .UseOpenTelemetry(configure: telemetry => telemetry.EnableSensitiveData = false)
            .UseLogging()
            .Build(services);
    }
}
