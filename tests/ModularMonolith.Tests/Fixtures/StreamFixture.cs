using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModularMonolith.Tests.StreamHost;
using ModularMonolith.Tests.Streams;
using Npgsql;
using PouryaKit.Messaging.Wolverine.Marten;
using PouryaKit.Modularity;
using Testcontainers.PostgreSql;

namespace ModularMonolith.Tests.Fixtures;

/// <summary>
/// One PostgreSQL for the Stream write model's tests, which records commit timestamps and takes many connections: each
/// test composes its Hosts over a database of its own, one of the Stream Module Accounts alone, or the Proving Sample's with
/// Accounts beside its EF Core Modules on one message store.
/// </summary>
public sealed class StreamFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.6")
        .WithCommand("-c", "track_commit_timestamp=on", "-c", "max_connections=300")
        .Build();

    /// <summary>Every log entry the composed Hosts have written.</summary>
    public RecordedLogs Logs { get; } = new();

    /// <summary>A new, empty database on the server, by its connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"streams_{Guid.CreateVersion7():N}";
        await Sql.RowsAsync(_postgres.GetConnectionString(), $"create database {name}");
        return new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = name }.ConnectionString;
    }

    /// <summary>The Host of Accounts alone over <paramref name="connectionString"/>, built and not started.</summary>
    public IHost MartenOnly(string connectionString, Action<IServiceCollection>? services = null)
    {
        var builder = StreamHosts.MartenOnly(connectionString);
        builder.Logging.AddProvider(Logs);
        services?.Invoke(builder.Services);
        return builder.Build();
    }

    /// <summary>
    /// The Proving Sample's Host with Accounts beside its EF Core Modules over <paramref name="connectionString"/>, its
    /// Marten store over the connection string <paramref name="martenConnection"/> integrated in the schema
    /// <paramref name="martenSchema"/>, built and not started; <paramref name="arguments"/> add to its configuration.
    /// </summary>
    public WebApplication Mixed(
        string connectionString,
        string martenConnection = "app",
        string martenSchema = "wolverine",
        Action<WebApplicationBuilder>? configure = null,
        params string[] arguments
    ) =>
        AppFixture.ComposeHost(
            "Development",
            connectionString,
            Logs,
            builder =>
            {
                builder.AddPouryaKitModules(modules => modules.Add<AccountsModule>());
                builder.UsePouryaKitMarten(martenConnection, martenSchema);
                configure?.Invoke(builder);
            },
            arguments
        );

    /// <inheritdoc />
    public async ValueTask InitializeAsync() => await _postgres.StartAsync(TestContext.Current.CancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();
}
