using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using S2.Catalog;
using S2.Host;
using S2.Kit;
using Testcontainers.PostgreSql;

[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)] // one collection: tests run one at a time
[assembly: AssemblyFixture(typeof(S2.Tests.PostgresServer))]

namespace S2.Tests;

/// <summary>One PostgreSQL server for the run; every test gets its own fresh database on it.</summary>
public sealed class PostgresServer : IAsyncLifetime
{
    public const string Image = "postgres:18.6";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage(Image)
        .WithCommand("-c", "track_commit_timestamp=on")
        .Build();

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    public async Task<SpikeDatabase> CreateDatabaseAsync()
    {
        var name = "s2_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = NpgsqlDataSource.Create(_container.GetConnectionString()))
        await using (var cmd = admin.CreateCommand($"create database {name}"))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        var cs = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
        await SpikeHost.CreateBusinessSchemaAsync(cs);
        return new SpikeDatabase(cs);
    }
}

/// <summary>Reads the database from a separate connection, outside any Spike transaction.</summary>
public sealed class SpikeDatabase(string connectionString) : IAsyncDisposable
{
    private readonly NpgsqlDataSource _ds = NpgsqlDataSource.Create(connectionString);

    public string ConnectionString => connectionString;

    public IHost BuildHost(SpikeSettings settings) =>
        SpikeHost.Build(settings, l => l.AddProvider(new TestOutputLoggerProvider()).SetMinimumLevel(LogLevel.Warning));

    public IHost BuildHost(DeliveryChoice delivery) => BuildHost(new SpikeSettings(connectionString, delivery));

    public async Task<IHost> StartHostAsync(SpikeSettings settings)
    {
        var host = BuildHost(settings);
        await host.StartAsync();
        return host;
    }

    public Task<IHost> StartHostAsync(DeliveryChoice delivery) => StartHostAsync(new SpikeSettings(connectionString, delivery));

    public static async Task PublishAsync(IHost host, Guid productId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await CatalogModule.CreateProductAsync(scope.ServiceProvider.GetRequiredService<UnitOfWork<CatalogDbContext>>(), productId);
    }

    public async Task<long> CountAsync(string sql, params object[] args)
    {
        await using var cmd = _ds.CreateCommand(sql);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task<List<T>> ListAsync<T>(string sql, Func<NpgsqlDataReader, T> map, params object[] args)
    {
        await using var cmd = _ds.CreateCommand(sql);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync()) rows.Add(map(reader));
        return rows;
    }

    /// <summary>Every invocation of a handler for one product, in order, as the outcome it logged.</summary>
    public Task<List<string>> InvocationsAsync(string handler, Guid productId) => ListAsync(
        "select outcome from spike.deliveries where handler = $1 and product_id = $2 order by id",
        r => r.GetString(0), handler, productId);

    /// <summary>
    /// The subscription an envelope row is for: Option B carries it in the body (its rows share one queue), Option A
    /// only in the queue name.
    /// </summary>
    private const string SubscriptionSql = """
        coalesce(substring(encode(body, 'escape') from '(?i)"subscription"\s*:\s*"([^"]+)"'),
                 substring(received_at from 'local://([^/]+)/'), received_at)
        """;

    public Task<List<Guid>> ScopesAsync(string handler, Guid productId) => ListAsync(
        "select scope_id from spike.deliveries where handler = $1 and product_id = $2 order by id",
        r => r.GetGuid(0), handler, productId);

    public Task<List<EnvelopeRow>> IncomingAsync() => ListAsync(
        $"select id, status, received_at, message_type, attempts, owner_id, {SubscriptionSql} as s from wolverine.wolverine_incoming_envelopes order by s",
        r => new EnvelopeRow(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5), r.GetString(6)));

    public Task<List<DeadLetterRow>> DeadLettersAsync() => ListAsync(
        $"select id, received_at, message_type, exception_type, exception_message, replayable, {SubscriptionSql} as s from wolverine.wolverine_dead_letters order by s",
        r => new DeadLetterRow(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), !r.IsDBNull(5) && r.GetBoolean(5), r.GetString(6)));

    public ValueTask DisposeAsync() => _ds.DisposeAsync();
}

public sealed record EnvelopeRow(Guid Id, string Status, string? ReceivedAt, string MessageType, int Attempts, int OwnerId, string Subscription);

public sealed record DeadLetterRow(Guid Id, string? ReceivedAt, string MessageType, string? ExceptionType, string? ExceptionMessage, bool Replayable, string Subscription);

public static class Eventually
{
    public static async Task<bool> WaitAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(100);
        }
        return await condition();
    }
}

/// <summary>Routes host logs (warnings and up) to the current test's output.</summary>
public sealed class TestOutputLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(categoryName);
    public void Dispose() { }

    private sealed class Logger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            try
            {
                TestContext.Current.TestOutputHelper?.WriteLine($"[{logLevel}] {category}: {formatter(state, exception)} {exception?.GetType().Name}: {exception?.Message}");
            }
            catch (InvalidOperationException)
            {
                // No test is running (host shutting down after the test finished).
            }
        }
    }
}
