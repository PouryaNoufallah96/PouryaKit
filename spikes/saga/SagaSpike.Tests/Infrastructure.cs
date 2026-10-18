using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;
using SagaSpike.Host;
using SagaSpike.Orders;
using Testcontainers.PostgreSql;
using Wolverine;

[assembly: AssemblyFixture(typeof(SagaSpike.Tests.Postgres))]

namespace SagaSpike.Tests;

/// <summary>One PostgreSQL for the run, which records commit timestamps; each test gets a database of its own.</summary>
public sealed class Postgres : IAsyncLifetime
{
    // The EF Modules' tables, as their migrations and ReceiptTable would create them.
    private const string ModuleTables = """
        create schema orders;
        create table orders.orders (id uuid primary key, installments integer not null, pay_within_seconds integer not null, version bigint not null);
        create table orders.pouryakit_receipts (subscription text not null, message_id uuid not null, primary key (subscription, message_id));
        create schema billing;
        create table billing.pouryakit_receipts (subscription text not null, message_id uuid not null, primary key (subscription, message_id));
        """;

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18.6")
        .WithCommand("-c", "track_commit_timestamp=on", "-c", "max_connections=300")
        .Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"saga_{Guid.NewGuid():N}";
        await Db.ExecuteAsync(_container.GetConnectionString(), $"create database {name}");
        var connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
        await Db.ExecuteAsync(connectionString, ModuleTables);
        return connectionString;
    }
}

/// <summary>A test that starts Hosts over a database of its own and stops them when it ends.</summary>
public abstract class SpikeTest(Postgres postgres) : IAsyncLifetime
{
    /// <summary>The fulfilment saga's name and queue.</summary>
    protected const string Fulfilment = "shipping.fulfilment";

    private readonly List<IHost> _hosts = [];

    protected string ConnectionString { get; private set; } = "";

    protected static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => ConnectionString = await postgres.CreateDatabaseAsync();

    protected async Task<IHost> StartAsync(SpikeHostOptions? options = null)
    {
        var host = SpikeHost.Build(ConnectionString, options);
        _hosts.Add(host);
        await host.StartAsync(Token);
        return host;
    }

    protected static OperationContext Context(string tenant) => new(Actor.Person("spike-tests"), tenant);

    protected static async Task PlaceAsync(IHost host, OperationContext context, Guid order, int installments = 2, int payWithinSeconds = 3600)
    {
        var placed = await host
            .Services.GetRequiredService<IGatewayProcessor<OrdersModule>>()
            .SendAsync(new PlaceOrderCommand(order, installments, payWithinSeconds), context, Token);
        Assert.True(placed.IsSuccess);
    }

    protected static async Task PayAsync(IHost host, OperationContext context, Guid order)
    {
        var paid = await host.Services.GetRequiredService<IGatewayProcessor<OrdersModule>>().SendAsync(new RecordPaymentCommand(order), context, Token);
        Assert.True(paid.IsSuccess);
    }

    /// <summary>
    /// Publishes <paramref name="integrationEvent"/> as the Kit's Outbox would write it for an Operation of
    /// <paramref name="context"/>: its Operation Context headers and the identity <paramref name="eventId"/>, under a new
    /// envelope id, as a broker's fan-out after a crash, or a republish, would deliver it again.
    /// </summary>
    protected static async Task PublishAsync(IHost host, object integrationEvent, Guid eventId, OperationContext context)
    {
        var options = new DeliveryOptions().WithOperationContext(context);
        options.Headers[IntegrationEventHeaders.DomainEventId] = eventId.ToString();
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().PublishAsync(integrationEvent, options);
    }

    /// <summary>The fulfilment saga's state by its identity, or null when no row holds it.</summary>
    protected async Task<JsonElement?> SagaAsync(string sagaId, string schema = "wolverine")
    {
        var rows = await Db.RowsAsync(ConnectionString, $"select body::text from {schema}.fulfilmentsaga_saga where id = $1", sagaId);
        return rows.Count == 0 ? null : JsonDocument.Parse((string)rows[0][0]!).RootElement;
    }

    /// <summary>How many payments the fulfilment saga <paramref name="sagaId"/> counted, or null when no row holds it.</summary>
    protected async Task<int?> PaidAsync(string sagaId) => (await SagaAsync(sagaId))?.GetProperty("Paid").GetInt32();

    protected Task<long> CountAsync(string sql, params object[] parameters) => Db.ScalarAsync<long>(ConnectionString, sql, parameters);

    protected Task WaitUntilAsync(string sql, params object[] parameters) => Db.WaitUntilAsync(ConnectionString, sql, TimeSpan.FromSeconds(30), parameters);

    /// <summary>Waits until no delivery is pending: every stored envelope handled or set aside, no retry waiting.</summary>
    protected Task SettledAsync(string schema = "wolverine") =>
        WaitUntilAsync(
            $"select count(*) = 0 from {schema}.wolverine_incoming_envelopes where status <> 'Handled' and (status <> 'Scheduled' or message_type not like '%FulfilmentTimeout%')"
        );

    /// <summary>The identity of the input the saga claimed under <paramref name="wireName"/>.</summary>
    protected async Task<Guid> ClaimedAsync(string sagaId, string wireName, string schema = "wolverine")
    {
        var rows = await Db.RowsAsync(ConnectionString, $"select event_id from {schema}.pouryakit_saga_steps where saga_id = $1 and input = $2", sagaId, wireName);
        return (Guid)Assert.Single(rows)[0]!;
    }

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

/// <summary>A child process of the Spike's Host, killed or awaited by a test.</summary>
public static class Child
{
    /// <summary>Starts <c>SagaSpike.Host</c> with <paramref name="args"/> and returns once it printed <paramref name="line"/>.</summary>
    public static async Task<Process> StartUntilAsync(string line, params string[] args)
    {
        var child = Process.Start(
            new ProcessStartInfo("dotnet", [typeof(SpikeHost).Assembly.Location, .. args])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        )!;
        child.ErrorDataReceived += (_, _) => { };
        child.BeginErrorReadLine();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (await child.StandardOutput.ReadLineAsync(timeout.Token) is { } printed && printed != line)
        {
        }

        return child;
    }

    /// <summary>Kills <paramref name="child"/> as a crash would (SIGKILL) and waits for it to exit.</summary>
    public static async Task KillAsync(Process child)
    {
        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync();
        child.Dispose();
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
        while (!await TryScalarAsync(connectionString, sql, parameters))
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException($"Still false after {timeout}: {sql}");
            }

            await Task.Delay(100);
        }
    }

    // A table Wolverine or the Kit has not created yet answers false.
    private static async Task<bool> TryScalarAsync(string connectionString, string sql, object[] parameters)
    {
        try
        {
            return await ScalarAsync<bool>(connectionString, sql, parameters);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return false;
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
