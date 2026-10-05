using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;
using Wolverine.Persistence;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
[assembly: AssemblyFixture(typeof(S1.Tests.PostgresServer))]

namespace S1.Tests;

/// <summary>One PostgreSQL server for the run; every test gets its own fresh database on it.</summary>
public sealed class PostgresServer : IAsyncLifetime
{
    public const string Image = "postgres:18.6";

    // track_commit_timestamp lets a test tell whether two rows were committed by the same transaction:
    // xmin alone cannot, because EF's automatic savepoint gives rows of one transaction different xids.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage(Image)
        .WithCommand("-c", "track_commit_timestamp=on")
        .Build();

    public string AdminConnectionString => _container.GetConnectionString();

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    /// <summary>Creates an empty database holding the Modules' business schemas; Wolverine adds its own at host start.</summary>
    public async Task<SpikeDatabase> CreateDatabaseAsync()
    {
        var name = "s1_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = NpgsqlDataSource.Create(AdminConnectionString))
        await using (var cmd = admin.CreateCommand($"create database {name}"))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        var cs = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = name }.ConnectionString;
        await SpikeHost.CreateBusinessSchemaAsync(cs);
        return new SpikeDatabase(cs);
    }
}

/// <summary>Reads the database from a separate connection, outside any Spike transaction.</summary>
public sealed class SpikeDatabase(string connectionString) : IAsyncDisposable
{
    private readonly NpgsqlDataSource _ds = NpgsqlDataSource.Create(connectionString);

    public string ConnectionString => connectionString;

    public IHost BuildHost(
        TransactionMiddlewareMode mode = TransactionMiddlewareMode.Eager,
        EnvelopeMapping mapping = EnvelopeMapping.Raw,
        Wolverine.DurabilityMode durability = Wolverine.DurabilityMode.Balanced,
        bool shortDurabilityTimers = true) =>
        SpikeHost.Build(new SpikeSettings(connectionString, mode, mapping, durability, shortDurabilityTimers),
            l => l.AddProvider(new TestOutputLoggerProvider()).SetMinimumLevel(LogLevel.Warning));

    public async Task<long> CountAsync(string sql, params object[] args)
    {
        await using var cmd = _ds.CreateCommand(sql);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task<T?> ScalarAsync<T>(string sql, params object[] args)
    {
        await using var cmd = _ds.CreateCommand(sql);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    public async Task<List<EnvelopeRow>> IncomingEnvelopesAsync()
    {
        await using var cmd = _ds.CreateCommand(
            $"select id, status, owner_id, received_at, message_type, xmin::text, pg_xact_commit_timestamp(xmin) from {SpikeHost.MessageStoreSchema}.wolverine_incoming_envelopes order by id");
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<EnvelopeRow>();
        while (await reader.ReadAsync())
            rows.Add(new EnvelopeRow(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetFieldValue<DateTime>(6)));
        return rows;
    }

    /// <summary>When the transaction that last wrote the row committed.</summary>
    public async Task<DateTime> CommittedAtAsync(string table, Guid id) =>
        (await ScalarAsync<DateTime?>($"select pg_xact_commit_timestamp(xmin) from {table} where id = $1", id))
        ?? throw new InvalidOperationException($"no row {id} in {table}");

    public Task<long> ProductsAsync(Guid id) => CountAsync("select count(*) from catalog.products where id = $1", id);
    public Task<long> StockItemsAsync(Guid id) => CountAsync("select count(*) from inventory.stock_items where id = $1", id);
    public Task<long> DeliveriesAsync(Guid entityId) => CountAsync("select count(*) from spike.deliveries where entity_id = $1", entityId);
    public Task<long> IncomingCountAsync() => CountAsync($"select count(*) from {SpikeHost.MessageStoreSchema}.wolverine_incoming_envelopes");
    public Task<long> OutgoingCountAsync() => CountAsync($"select count(*) from {SpikeHost.MessageStoreSchema}.wolverine_outgoing_envelopes");

    public ValueTask DisposeAsync() => _ds.DisposeAsync();
}

public sealed record EnvelopeRow(Guid Id, string Status, int OwnerId, string? ReceivedAt, string MessageType, string Xmin, DateTime CommittedAt);

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
                TestContext.Current.TestOutputHelper?.WriteLine($"[{logLevel}] {category}: {formatter(state, exception)} {exception}");
            }
            catch (InvalidOperationException)
            {
                // No test is running (host shutting down after the test finished).
            }
        }
    }
}
