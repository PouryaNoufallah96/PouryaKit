using Npgsql;

namespace PouryaKit.Testing;

// Reads deliveries from the Host's message store itself, the record that survives a restart: a delivery is pending
// while its envelope is neither handled nor dead-lettered, unless it is a delayed command not yet due, scheduled and
// never attempted (a stored retry is scheduled too, with its attempts counted), or while it is a dead letter marked for
// replay that the store has not yet moved back, and every envelope an Operation's commit wrote has a row.
internal sealed class StoredDeliveries(
    string connectionString,
    string schema,
    IReadOnlyList<(string Schema, string Name)> storeTables
)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);
    private readonly string _quotedSchema =
        $"\"{schema.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    /// <summary>The tables the message store creates for itself, as it names them.</summary>
    public IReadOnlyList<(string Schema, string Name)> StoreTables => storeTables;

    public async Task<HashSet<Guid>> IdsAsync(CancellationToken cancellationToken) =>
        [
            .. (
                await RowsAsync(
                    $"select id from {_quotedSchema}.wolverine_incoming_envelopes union select id from {_quotedSchema}.wolverine_dead_letters",
                    cancellationToken
                )
            ).Select(row => (Guid)row[0]),
        ];

    public async Task<List<(Guid Id, Delivery Delivery)>> AllAsync(
        CancellationToken cancellationToken
    ) =>
        [
            .. (
                await RowsAsync(
                    $"""
                    select id, received_at, message_type, false from {_quotedSchema}.wolverine_incoming_envelopes
                    union all
                    select id, received_at, message_type, true from {_quotedSchema}.wolverine_dead_letters
                    order by 1
                    """,
                    cancellationToken
                )
            ).Select(row =>
                (
                    (Guid)row[0],
                    new Delivery(SubscriptionAt((string)row[1]), (string)row[2], (bool)row[3])
                )
            ),
        ];

    /// <summary>
    /// Every envelope the store holds for a sending agent, as <c>{message type} to {destination}</c>: written in a commit
    /// and not yet sent, such as while its broker is unavailable. A sent one is deleted.
    /// </summary>
    public async Task<List<string>> OutgoingAsync(CancellationToken cancellationToken) =>
        [
            .. (
                await RowsAsync(
                    $"select message_type, destination from {_quotedSchema}.wolverine_outgoing_envelopes order by id",
                    cancellationToken
                )
            ).Select(row => $"{row[0]} to {row[1]}"),
        ];

    /// <summary>
    /// The subscription a delivery received at <paramref name="receivedAt"/> was for: the name of its local queue,
    /// <c>local://{subscription}/</c>. A delivery received at a broker queue, such as <c>rabbitmq://queue/{ServiceName}</c>,
    /// was for no subscription yet, so it is named by that address.
    /// </summary>
    public static string SubscriptionAt(string receivedAt)
    {
        var address = new Uri(receivedAt);
        return address.Scheme == "local" ? address.Host : receivedAt;
    }

    /// <summary>
    /// Waits until no delivery is pending: every stored envelope is handled, dead-lettered or a delayed command not yet due, and no
    /// replay is waiting.
    /// </summary>
    public async Task WaitUntilSettledAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (await PendingAsync(deadline.Token) > 0)
            {
                await Task.Delay(PollInterval, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Deliveries were still pending after {timeout.TotalSeconds} s: {await PendingAsync(CancellationToken.None)} envelopes in {schema} are neither handled, dead-lettered nor delayed commands not yet due, or wait to be replayed."
            );
        }
    }

    public async Task DeleteAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"delete from {_quotedSchema}.wolverine_incoming_envelopes; delete from {_quotedSchema}.wolverine_outgoing_envelopes; delete from {_quotedSchema}.wolverine_dead_letters",
            connection
        );
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long> PendingAsync(CancellationToken cancellationToken) =>
        (long)
            (
                await RowsAsync(
                    $"select (select count(*) from {_quotedSchema}.wolverine_incoming_envelopes where status <> 'Handled' and not (status = 'Scheduled' and attempts = 0 and execution_time > now())) + (select count(*) from {_quotedSchema}.wolverine_outgoing_envelopes) + (select count(*) from {_quotedSchema}.wolverine_dead_letters where replayable)",
                    cancellationToken
                )
            )[0][0];

    private async Task<List<object[]>> RowsAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<object[]> rows = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row);
        }

        return rows;
    }
}
