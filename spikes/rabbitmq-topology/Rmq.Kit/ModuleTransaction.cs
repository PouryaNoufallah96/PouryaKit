using Npgsql;

namespace Rmq.Kit;

/// <summary>
/// Spike stand-in for the receiving Module's Unit of Work in a delivery scope: one connection and one transaction, in
/// which the Kit records the Receipt and the handler applies its effect, so both commit or neither does.
/// </summary>
public sealed class ModuleTransaction(NpgsqlDataSource dataSource) : IAsyncDisposable
{
    private NpgsqlConnection? _connection;
    private NpgsqlTransaction? _transaction;

    public async Task BeginAsync(CancellationToken cancellationToken)
    {
        _connection = await dataSource.OpenConnectionAsync(cancellationToken);
        _transaction = await _connection.BeginTransactionAsync(cancellationToken);
    }

    /// <summary>Runs a statement in the transaction; the handler's effect.</summary>
    public async Task<int> ExecuteAsync(string sql, params object[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, _connection, _transaction);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Records the Receipt of (subscription, message identity). False when it already exists: a redelivery after the
    /// commit. A concurrent copy waits on the row lock until this transaction ends.
    /// </summary>
    public async Task<bool> RecordReceiptAsync(string subscription, Guid messageId) =>
        await ExecuteAsync(
            "insert into inventory.receipts (subscription, message_id) values ($1, $2) on conflict do nothing",
            subscription,
            messageId
        ) == 1;

    public Task CommitAsync() => _transaction!.CommitAsync();

    public Task RollbackAsync() => _transaction!.RollbackAsync();

    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }
}

/// <summary>What the delivery step knows about the attempt it runs, for the Spike's failing handler.</summary>
public sealed class DeliveryAttempt
{
    public string Subscription { get; set; } = "";

    public Guid MessageId { get; set; }

    /// <summary>How many earlier attempts of this subscription's delivery of this message failed.</summary>
    public int PriorFailures { get; set; }
}

/// <summary>
/// Every delivery attempt with its outcome, written on its own connection outside the delivery's transaction. Receipts
/// mask a rerun of a subscription that succeeded, so the tests read this log to see whether one happened.
/// </summary>
public sealed class AttemptLog(NpgsqlDataSource dataSource)
{
    public async Task AddAsync(string subscription, Guid messageId, Guid envelopeId, string receivedAt, string outcome)
    {
        await using var command = dataSource.CreateCommand(
            "insert into spike.attempts (subscription, message_id, envelope_id, received_at, outcome) values ($1, $2, $3, $4, $5)"
        );
        command.Parameters.AddWithValue(subscription);
        command.Parameters.AddWithValue(messageId);
        command.Parameters.AddWithValue(envelopeId);
        command.Parameters.AddWithValue(receivedAt);
        command.Parameters.AddWithValue(outcome);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<int> FailuresAsync(string subscription, Guid messageId)
    {
        await using var command = dataSource.CreateCommand(
            "select count(*) from spike.attempts where subscription = $1 and message_id = $2 and outcome = 'failed'"
        );
        command.Parameters.AddWithValue(subscription);
        command.Parameters.AddWithValue(messageId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}

/// <summary>The consumer's schema: the Module's Receipts and effects, and the Spike's attempt log.</summary>
public static class ConsumerSchema
{
    public const string Sql = """
        create schema if not exists inventory;
        create schema if not exists spike;
        create table if not exists inventory.receipts (
            subscription text not null,
            message_id uuid not null,
            primary key (subscription, message_id));
        create table if not exists inventory.effects (
            id bigserial primary key,
            subscription text not null,
            product_id uuid not null);
        create table if not exists spike.attempts (
            id bigserial primary key,
            subscription text not null,
            message_id uuid not null,
            envelope_id uuid not null,
            received_at text not null,
            outcome text not null,
            at timestamptz not null default clock_timestamp());
        """;
}
