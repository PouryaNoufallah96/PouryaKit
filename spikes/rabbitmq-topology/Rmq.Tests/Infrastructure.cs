using Microsoft.Extensions.Logging;
using Npgsql;
using RabbitMQ.Client;
using Rmq.Catalog;
using Rmq.Kit;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)] // one collection: tests run one at a time
[assembly: AssemblyFixture(typeof(Rmq.Tests.Servers))]

namespace Rmq.Tests;

/// <summary>One PostgreSQL server and one RabbitMQ broker for the run. Every test gets two fresh databases and a vhost.</summary>
public sealed class Servers : IAsyncLifetime
{
    public const string PostgresImage = "postgres:18.6";
    public const string RabbitMqImage = "rabbitmq:4.3.6";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(PostgresImage)
        .WithCommand("-c", "track_commit_timestamp=on", "-c", "max_connections=1000")
        .Build();

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder(RabbitMqImage).Build();

    public async ValueTask InitializeAsync() => await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());

    public async ValueTask DisposeAsync()
    {
        await _rabbit.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    public async Task<TestEnvironment> CreateEnvironmentAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var producer = await CreateDatabaseAsync($"catalog_{suffix}", CatalogModule.SchemaSql);
        var consumer = await CreateDatabaseAsync($"inventory_{suffix}", ConsumerSchema.Sql);

        var vhost = $"t{suffix}";
        await RabbitCtlAsync("add_vhost", vhost);
        var user = new Uri(_rabbit.GetConnectionString()).UserInfo.Split(':')[0];
        await RabbitCtlAsync("set_permissions", "-p", vhost, user, ".*", ".*", ".*");
        var broker = new UriBuilder(_rabbit.GetConnectionString()) { Path = "/" + vhost }.Uri;
        return new TestEnvironment(producer, consumer, broker);
    }

    /// <summary>Another subscribing service on the same broker vhost and producer: a database of its own.</summary>
    public async Task<TestEnvironment> AddConsumerAsync(TestEnvironment environment) =>
        new(
            environment.ProducerDatabase,
            await CreateDatabaseAsync($"other_{Guid.NewGuid().ToString("N")[..10]}", ConsumerSchema.Sql),
            environment.Broker
        );

    private async Task RabbitCtlAsync(params string[] arguments)
    {
        var result = await _rabbit.ExecAsync(["rabbitmqctl", .. arguments]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"rabbitmqctl {string.Join(' ', arguments)}: {result.Stderr}");
        }
    }

    private async Task<string> CreateDatabaseAsync(string name, string schemaSql)
    {
        await using (var admin = NpgsqlDataSource.Create(_postgres.GetConnectionString()))
        await using (var create = admin.CreateCommand($"create database {name}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = name }.ConnectionString;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var schema = dataSource.CreateCommand(schemaSql);
        await schema.ExecuteNonQueryAsync();
        return connectionString;
    }
}

/// <summary>One test's producer database, consumer database and broker vhost, with readers that use their own connections.</summary>
public sealed class TestEnvironment(string producerDatabase, string consumerDatabase, Uri broker) : IAsyncDisposable
{
    private readonly NpgsqlDataSource _producer = NpgsqlDataSource.Create(producerDatabase);
    private readonly NpgsqlDataSource _consumer = NpgsqlDataSource.Create(consumerDatabase);

    public string ProducerDatabase => producerDatabase;

    public string ConsumerDatabase => consumerDatabase;

    public Uri Broker => broker;

    public Task<long> ProducerCountAsync(string sql, params object[] parameters) => CountAsync(_producer, sql, parameters);

    public Task<long> ConsumerCountAsync(string sql, params object[] parameters) => CountAsync(_consumer, sql, parameters);

    public Task<List<T>> ProducerListAsync<T>(string sql, Func<NpgsqlDataReader, T> map, params object[] parameters) =>
        ListAsync(_producer, sql, map, parameters);

    public Task<List<T>> ConsumerListAsync<T>(string sql, Func<NpgsqlDataReader, T> map, params object[] parameters) =>
        ListAsync(_consumer, sql, map, parameters);

    public Task<int> ConsumerExecuteAsync(string sql, params object[] parameters) => ExecuteAsync(_consumer, sql, parameters);

    /// <summary>Committed effects of one subscription for one product.</summary>
    public Task<long> EffectsAsync(string subscription, Guid productId) =>
        ConsumerCountAsync("select count(*) from inventory.effects where subscription = $1 and product_id = $2", subscription, productId);

    /// <summary>Committed Receipts of one subscription for one message identity.</summary>
    public Task<long> ReceiptsAsync(string subscription, Guid messageId) =>
        ConsumerCountAsync("select count(*) from inventory.receipts where subscription = $1 and message_id = $2", subscription, messageId);

    /// <summary>Every attempt's outcome of one subscription for one message, in order.</summary>
    public Task<List<string>> AttemptsAsync(string subscription, Guid messageId) =>
        ConsumerListAsync(
            "select outcome from spike.attempts where subscription = $1 and message_id = $2 order by id",
            reader => reader.GetString(0),
            subscription,
            messageId
        );

    /// <summary>The consumer store's inbox rows.</summary>
    public Task<List<InboxRow>> InboxAsync() =>
        ConsumerListAsync(
            "select id, status, received_at, message_type, owner_id from wolverine.wolverine_incoming_envelopes order by received_at",
            reader => new InboxRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4))
        );

    public Task<List<DeadLetterRow>> DeadLettersAsync() =>
        ConsumerListAsync(
            "select id, received_at, message_type, exception_type from wolverine.wolverine_dead_letters order by received_at",
            reader => new DeadLetterRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3))
        );

    /// <summary>Messages ready in a broker queue, read with a passive declare.</summary>
    public async Task<uint> BrokerMessagesAsync(string queue)
    {
        var factory = new ConnectionFactory { Uri = broker };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        return await channel.MessageCountAsync(queue);
    }

    /// <summary>True when the broker has a queue of that name in this vhost.</summary>
    public async Task<bool> BrokerQueueExistsAsync(string queue)
    {
        var factory = new ConnectionFactory { Uri = broker };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        try
        {
            await channel.QueueDeclarePassiveAsync(queue);
            return true;
        }
        catch (RabbitMQ.Client.Exceptions.OperationInterruptedException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _producer.DisposeAsync();
        await _consumer.DisposeAsync();
    }

    private static async Task<long> CountAsync(NpgsqlDataSource dataSource, string sql, object[] parameters)
    {
        await using var command = dataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<int> ExecuteAsync(NpgsqlDataSource dataSource, string sql, object[] parameters)
    {
        await using var command = dataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<T>> ListAsync<T>(NpgsqlDataSource dataSource, string sql, Func<NpgsqlDataReader, T> map, object[] parameters)
    {
        await using var command = dataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        await using var reader = await command.ExecuteReaderAsync();
        List<T> rows = [];
        while (await reader.ReadAsync())
        {
            rows.Add(map(reader));
        }

        return rows;
    }
}

public sealed record InboxRow(Guid Id, string Status, string ReceivedAt, string MessageType, int OwnerId);

public sealed record DeadLetterRow(Guid Id, string ReceivedAt, string MessageType, string? ExceptionType);

public static class Eventually
{
    public static async Task<bool> WaitAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return await condition();
    }
}

/// <summary>Routes Host logs (warnings and up) to the current test's output.</summary>
public sealed class TestOutputLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(categoryName);

    public void Dispose() { }

    private sealed class Logger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            try
            {
                TestContext.Current.TestOutputHelper?.WriteLine($"[{logLevel}] {category}: {formatter(state, exception)} {exception?.GetType().Name}: {exception?.Message}");
            }
            catch (InvalidOperationException)
            {
                // No test is running (a Host shutting down after its test finished).
            }
        }
    }
}
