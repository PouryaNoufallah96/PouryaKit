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

    /// <summary>
    /// The name of the HTTP client the Host's MCP servers over HTTP are reached through, apart from the model's, so the Host
    /// configures it by this name, as with an authentication handler, without sending what it adds to the model's provider.
    /// </summary>
    public const string McpHttpClientName = "PouryaKit.AI.Mcp";

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
        /// every <see cref="IChatGuardrail"/> the Host registers, none by default. Its OpenTelemetry step carries no message
        /// content, and its logging step records the messages and options when the Host's logger enables Trace, as
        /// Microsoft.Extensions.AI documents. The Host binds the options from its configuration, as with
        /// <c>Configure&lt;PouryaKitAIOptions&gt;(configuration.GetSection("PouryaKit:AI"))</c>; <paramref name="configure"/>
        /// sets them in code instead, and with <see cref="AddPouryaKitEmbeddings"/> too, each call's applies, in the order
        /// of the calls. The Host fails when it starts,
        /// naming the option, when the provider is not an <see cref="AIProvider"/> value, the model is blank, the endpoint
        /// is not an absolute HTTP or HTTPS address or is missing for Azure OpenAI or Ollama, the key is missing for
        /// OpenAI or Azure OpenAI, the iteration cap is below 1 or a tool name is blank. A Host composes exactly one
        /// <see cref="IChatClient"/>: its build fails, through the Kit's composition checks, naming each registration
        /// when it registers another, before or after this call, a second call included, and naming the contract when it
        /// removes this one; it fails
        /// too, naming each, when it registers an <see cref="IChatGuardrail"/> that is not a singleton. It registers
        /// <see cref="IMcpServers"/>, the Host's clients of the MCP servers <see cref="PouryaKitAIOptions.McpServers"/> names,
        /// over stdio or over the HTTP client named <see cref="McpHttpClientName"/>, which the Host's services own and dispose,
        /// and the Host fails when it starts when a server has neither or both of a command and an endpoint, or an endpoint
        /// that is not an absolute HTTP or HTTPS address.
        /// </summary>
        public IServiceCollection AddPouryaKitAI(Action<PouryaKitAIOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            AddProvider(services, configure)
                .Validate(
                    ai => !string.IsNullOrWhiteSpace(ai.Model),
                    "PouryaKitAIOptions.Model names no model; set the model the Host's runs use, or for Azure OpenAI its deployment."
                )
                .Validate(
                    ai => ai.MaximumIterationsPerRun >= 1,
                    "PouryaKitAIOptions.MaximumIterationsPerRun is below 1; a run calls the model at least once."
                )
                .Validate(
                    ai => !ai.Tools.Any(string.IsNullOrWhiteSpace),
                    "PouryaKitAIOptions.Tools names a blank tool; name each tool a run may be given."
                )
                .Validate(
                    ai => ai.McpServers.Values.All(server => string.IsNullOrWhiteSpace(server.Command) != (server.Endpoint is null)),
                    "PouryaKitAIOptions.McpServers names a server with neither or both of Command and Endpoint; give each a Command to start it over stdio or an Endpoint to reach it over HTTP."
                )
                .Validate(
                    ai => ai.McpServers.Values.All(server => server.Endpoint is null || IsHttp(server.Endpoint)),
                    "PouryaKitAIOptions.McpServers names a server whose Endpoint is not an absolute HTTP or HTTPS address."
                );
            // An MCP server's client keeps its HttpClient as the OpenAI client keeps the model's, so connections are renewed
            // by their own lifetime instead of by the factory's handler rotation, as IHttpClientFactory's guidance asks of a
            // long-lived client.
            services
                .AddHttpClient(McpHttpClientName)
                .UseSocketsHttpHandler((handler, _) => handler.PooledConnectionLifetime = TimeSpan.FromMinutes(2))
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompositionCheck>(new OneProviderCheck<IChatClient>("AddPouryaKitAI", "chat client")));
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompositionCheck, SingletonGuardrailsCheck>());
            services.AddSingleton<IChatClient>(Create);
            services.AddSingleton<IMcpServers, McpServerConnections>();
            return services;
        }

        /// <summary>
        /// Registers the Host's <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> of text, a singleton that reaches the
        /// embeddings API of the provider of <see cref="PouryaKitAIOptions"/> for <see cref="PouryaKitAIOptions.EmbeddingModel"/>
        /// through the same OpenAI client as the chat client of <see cref="AddPouryaKitAI"/>, over the HTTP client named
        /// <see cref="HttpClientName"/>, with or without it. Its OpenTelemetry step carries no input, and its logging step
        /// records the input and options when the Host's logger enables Trace, as Microsoft.Extensions.AI documents. The
        /// Host binds the options as for the chat client; <paramref name="configure"/> sets them in code instead, and with
        /// <see cref="AddPouryaKitAI"/> too, each call's applies, in the order of the calls. The Host fails when it starts,
        /// naming the option, on the same provider, endpoint and key rules, and when the embedding model is blank.
        /// </summary>
        public IServiceCollection AddPouryaKitEmbeddings(Action<PouryaKitAIOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            AddProvider(services, configure)
                .Validate(
                    ai => !string.IsNullOrWhiteSpace(ai.EmbeddingModel),
                    "PouryaKitAIOptions.EmbeddingModel names no model; set the model the Host embeds text with, or for Azure OpenAI its deployment."
                );
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(CreateEmbeddingGenerator);
            return services;
        }
    }

    // The provider's rules, its HTTP client and its one OpenAI client, added once whether the Host composes the chat
    // client, the embedding generator or both; each registration's configure is added, the second's too.
    private static OptionsBuilder<PouryaKitAIOptions> AddProvider(IServiceCollection services, Action<PouryaKitAIOptions>? configure)
    {
        var options = services.AddOptions<PouryaKitAIOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        if (services.Any(descriptor => descriptor.ServiceType == typeof(Provider)))
        {
            return options;
        }

        options
            .Validate(
                ai => Enum.IsDefined(ai.Provider),
                "PouryaKitAIOptions.Provider is not an AIProvider value; set OpenAI, AzureOpenAI or Ollama."
            )
            .Validate(
                ai => ai.Endpoint is null || IsHttp(ai.Endpoint),
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
            .ValidateOnStart();
        // The OpenAI client is a singleton that keeps its HttpClient, so connections are renewed by their own lifetime
        // instead of by the factory's handler rotation, as IHttpClientFactory's guidance asks of a long-lived client.
        services
            .AddHttpClient(HttpClientName)
            .UseSocketsHttpHandler((handler, _) => handler.PooledConnectionLifetime = TimeSpan.FromMinutes(2))
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        services.AddSingleton(CreateProvider);
        return options;
    }

    private static Provider CreateProvider(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<PouryaKitAIOptions>>().Value;
        return new(
            new OpenAIClient(
                new ApiKeyCredential(string.IsNullOrWhiteSpace(options.ApiKey) ? UnusedKey : options.ApiKey),
                new OpenAIClientOptions
                {
                    Endpoint = options.Endpoint,
                    Transport = new HttpClientPipelineTransport(services.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName)),
                    RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
                }
            )
        );
    }

    // A named method, which the one-client check names. Function invocation sits inside the allow-list, so it never sees
    // a withheld tool, and outside the guardrails, telemetry and logging, which see each call to the model.
    private static IChatClient Create(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<PouryaKitAIOptions>>().Value;
        var client = new ChatClientBuilder(services.GetRequiredService<Provider>().Client.GetChatClient(options.Model).AsIChatClient())
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

    private static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<PouryaKitAIOptions>>().Value;
        return new EmbeddingGeneratorBuilder<string, Embedding<float>>(
            services.GetRequiredService<Provider>().Client.GetEmbeddingClient(options.EmbeddingModel).AsIEmbeddingGenerator()
        )
            .UseOpenTelemetry(configure: telemetry => telemetry.EnableSensitiveData = false)
            .UseLogging()
            .Build(services);
    }

    // The one OpenAI client of the Host's provider, which the chat client and the embedding generator share.
    private sealed record Provider(OpenAIClient Client);

    private static bool IsHttp(Uri endpoint) => endpoint.IsAbsoluteUri && (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps);
}
