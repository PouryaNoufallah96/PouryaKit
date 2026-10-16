using M2.Host;
using M2.Ledger;
using Marten;
using Marten.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(M2.Tests.Postgres))]

namespace M2.Tests;

/// <summary>One PostgreSQL for the run, which records commit timestamps; each test gets a database of its own.</summary>
public sealed class Postgres : IAsyncLifetime
{
    // The EF Module's Receipt and idempotency tables, as ReceiptTable and IdempotencyTable create them.
    private const string CatalogTables = """
        create schema catalog;
        create table catalog.pouryakit_receipts (subscription text not null, message_id uuid not null, primary key (subscription, message_id));
        create table catalog.pouryakit_idempotency_keys (tenant text not null, actor_kind text not null, actor_id text not null, command text not null, request_id text not null, response text, reserved_at timestamp with time zone not null, primary key (tenant, actor_kind, actor_id, command, request_id));
        """;

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18.6")
        .WithCommand("-c", "track_commit_timestamp=on", "-c", "max_connections=300")
        .Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"m2_{Guid.NewGuid():N}";
        await Db.ExecuteAsync(_container.GetConnectionString(), $"create database {name}");
        var connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
        await Db.ExecuteAsync(connectionString, CatalogTables);
        return connectionString;
    }
}

/// <summary>A test that starts Hosts over a database of its own and stops them when it ends.</summary>
public abstract class SpikeTest(Postgres postgres) : IAsyncLifetime
{
    private readonly List<IHost> _hosts = [];

    protected string ConnectionString { get; private set; } = "";

    protected static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => ConnectionString = await postgres.CreateDatabaseAsync();

    protected Task<string> CreateDatabaseAsync() => postgres.CreateDatabaseAsync();

    protected async Task<IHost> StartAsync(SpikeHostOptions? options = null)
    {
        var host = SpikeHost.Build(ConnectionString, options);
        _hosts.Add(host);
        await host.StartAsync(Token);
        return host;
    }

    protected IHost Build(SpikeHostOptions? options = null)
    {
        var host = SpikeHost.Build(ConnectionString, options);
        _hosts.Add(host);
        return host;
    }

    protected static OperationContext Context(string? tenant = null, string? requestId = null) =>
        new(Actor.System("m2-tests"), tenant) { RequestId = requestId };

    protected static IGatewayProcessor<LedgerModule> Ledger(IHost host) => host.Services.GetRequiredService<IGatewayProcessor<LedgerModule>>();

    protected Task<long> CountAsync(string sql, params object[] parameters) => Db.ScalarAsync<long>(ConnectionString, sql, parameters);

    protected Task WaitUntilAsync(string sql, params object[] parameters) => Db.WaitUntilAsync(ConnectionString, sql, TimeSpan.FromSeconds(30), parameters);

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            try
            {
                await host.StopAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                // A Host that never started, or failed to, has nothing to stop.
            }

            host.Dispose();
        }

        NpgsqlConnection.ClearAllPools();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Plain SQL on a connection of its own, outside every Host.</summary>
public static class Db
{
    public static async Task ExecuteAsync(string connectionString, string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = Command(connection, sql, parameters);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<T> ScalarAsync<T>(string connectionString, string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = Command(connection, sql, parameters);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    public static async Task<List<object?[]>> RowsAsync(string connectionString, string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = Command(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync();
        List<object?[]> rows = [];
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    public static async Task WaitUntilAsync(string connectionString, string sql, TimeSpan timeout, params object[] parameters)
    {
        var until = DateTime.UtcNow + timeout;
        while (!await ScalarAsync<bool>(connectionString, sql, parameters))
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException($"Still false after {timeout}: {sql}");
            }

            await Task.Delay(100);
        }
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, object[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        return command;
    }
}

/// <summary>
/// A store listener: it runs before each save, and after Marten's commit before the flush listener each MartenOutbox
/// adds to its session.
/// </summary>
public sealed class CommitProbe : DocumentSessionListenerBase
{
    public Func<Task>? OnBeforeSave { get; set; }

    public Func<Task>? OnCommit { get; set; }

    public override async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
    {
        if (OnBeforeSave is { } onBeforeSave)
        {
            await onBeforeSave();
        }
    }

    public override async Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        if (OnCommit is { } onCommit)
        {
            await onCommit();
        }
    }
}
