using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModularMonolith.Tests.Vectors;
using PouryaKit.AI;
using PouryaKit.Modularity;
using PouryaKit.Testing;
using Testcontainers.PostgreSql;

namespace ModularMonolith.Tests.Fixtures;

/// <summary>
/// PostgreSQL with pgvector, on the Kit's PostgreSQL major, and a Host of <see cref="VectorsModule"/> alone over it,
/// started, whose embedding generator embeds through the <see cref="ChatStub"/>, so every embedding is
/// <see cref="ChatStub.Embed"/>'s.
/// </summary>
public sealed class VectorFixture : IAsyncLifetime
{
    /// <summary>pgvector's own image on Docker Hub, pinned to its release and PostgreSQL major.</summary>
    public const string Image = "docker.io/pgvector/pgvector:0.8.7-pg18";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(Image).Build();
    private ChatStub? _stub;
    private IHost? _host;

    /// <summary>The model the Host embeds text with.</summary>
    public ChatStub Stub =>
        _stub ?? throw new InvalidOperationException("The stub starts in InitializeAsync.");

    /// <summary>The started Host.</summary>
    public IHost Host =>
        _host ?? throw new InvalidOperationException("The Host starts in InitializeAsync.");

    /// <summary>The database the Host stores in.</summary>
    public string ConnectionString => _postgres.GetConnectionString();

    /// <summary>
    /// A Host of <see cref="VectorsModule"/> over <paramref name="connectionString"/> whose embedding generator embeds
    /// through <paramref name="stub"/>, built and not started; <paramref name="configure"/> may add to its composition
    /// and <paramref name="arguments"/> to its configuration.
    /// </summary>
    public static IHost Compose(
        string connectionString,
        ChatStub stub,
        Action<IServiceCollection>? configure = null,
        params string[] arguments
    )
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder([
            "--environment",
            Environments.Development,
            $"--ConnectionStrings:app={connectionString}",
            "--PouryaKit:AI:Provider=Ollama",
            $"--PouryaKit:AI:Endpoint={stub.Endpoint}",
            "--PouryaKit:AI:EmbeddingModel=stub-embeddings",
            .. arguments,
        ]);
        builder.AddPouryaKitModules(modules => modules.Add<VectorsModule>());
        builder.Services.AddPouryaKitEmbeddings();
        builder.Services.Configure<PouryaKitAIOptions>(
            builder.Configuration.GetSection("PouryaKit:AI")
        );
        configure?.Invoke(builder.Services);
        return builder.Build();
    }

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync(TestContext.Current.CancellationToken);
        _stub = await ChatStub.StartAsync(TestContext.Current.CancellationToken);
        _host = Compose(ConnectionString, _stub);
        await HostFixture.StartHostAsync(_host, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            try
            {
                if (_host is not null)
                {
                    await _host.StopAsync(TestContext.Current.CancellationToken);
                    _host.Dispose();
                }
            }
            finally
            {
                if (_stub is not null)
                {
                    await _stub.DisposeAsync();
                }
            }
        }
        finally
        {
            await _postgres.DisposeAsync();
        }
    }
}
