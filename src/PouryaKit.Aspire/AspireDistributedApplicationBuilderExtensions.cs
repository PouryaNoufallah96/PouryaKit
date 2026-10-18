using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PouryaKit.Aspire;

/// <summary>
/// Adds to an AppHost the containers a Kit Host runs on locally: PostgreSQL always, Redis when the Host registers a
/// distributed cache, RabbitMQ when it sends or receives over that transport, Ollama when its agent runs locally. Each runs a pinned image, the same one the
/// Kit's own tests run, and keeps its data in a named volume; nothing else, no dashboard, tracing or logging resource.
/// </summary>
public static class AspireDistributedApplicationBuilderExtensions
{
    // The tags the Kit's Testcontainers fixtures run, so local infrastructure is what the tests prove. RabbitMQ's carries no
    // management plugin.
    private const string Registry = "docker.io";
    private const string PostgresTag = "18.6";
    private const string RedisTag = "8.10.1";
    private const string RabbitMqTag = "4.3.6";
    private const string OllamaTag = "0.40.6";

    extension(IDistributedApplicationBuilder builder)
    {
        /// <summary>
        /// Adds the PostgreSQL server named <paramref name="name"/>, on the pinned image with its data in a named volume; a
        /// Host's database is one <c>AddDatabase</c> of it, referenced under the Host's connection name.
        /// </summary>
        public IResourceBuilder<PostgresServerResource> AddPouryaKitPostgres([ResourceName] string name) =>
            builder.AddPostgres(name).WithImageRegistry(Registry).WithImage("library/postgres", PostgresTag).WithDataVolume();

        /// <summary>
        /// Adds the Redis server named <paramref name="name"/>, on the pinned image with its data in a named volume: the
        /// distributed cache behind HybridCache, for a Host that registers one.
        /// </summary>
        public IResourceBuilder<RedisResource> AddPouryaKitRedis([ResourceName] string name) =>
            builder.AddRedis(name).WithImageRegistry(Registry).WithImage("library/redis", RedisTag).WithDataVolume();

        /// <summary>
        /// Adds the RabbitMQ broker named <paramref name="name"/>, on the pinned image with its data in a named volume, for a
        /// Host that sends or receives Integration Events and Snapshots over RabbitMQ.
        /// </summary>
        public IResourceBuilder<RabbitMQServerResource> AddPouryaKitRabbitMq([ResourceName] string name) =>
            builder.AddRabbitMQ(name).WithImageRegistry(Registry).WithImage("library/rabbitmq", RabbitMqTag).WithDataVolume();

        /// <summary>
        /// Adds the Ollama server named <paramref name="name"/>, on the pinned image with its models in a named volume, healthy
        /// once it serves <paramref name="model"/>, which it pulls into the volume when it first starts; for a Host whose agent
        /// runs locally, given the endpoint as <c>PouryaKit:AI:Endpoint</c> with <c>/v1</c>, its OpenAI-compatible API.
        /// </summary>
        public IResourceBuilder<ContainerResource> AddPouryaKitOllama([ResourceName] string name, string model)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
            var ollama = builder
                .AddContainer(name, "ollama/ollama", OllamaTag)
                .WithImageRegistry(Registry)
                .WithHttpEndpoint(targetPort: 11434, name: "http");
            ollama.WithVolume(VolumeNameGenerator.Generate(ollama, "data"), "/root/.ollama");
            var endpoint = ollama.GetEndpoint("http");
            var check = $"{name}_model";
            builder.Services.AddHttpClient();
            builder
                .Services.AddHealthChecks()
                .Add(
                    new HealthCheckRegistration(
                        check,
                        services => new OllamaModelHealthCheck(endpoint, model, services.GetRequiredService<IHttpClientFactory>()),
                        failureStatus: null,
                        tags: null
                    )
                );
            return ollama.WithHealthCheck(check);
        }
    }
}
