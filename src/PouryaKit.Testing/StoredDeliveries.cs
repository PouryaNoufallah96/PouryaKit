using Npgsql;

namespace PouryaKit.Testing;

// Reads deliveries from the Host's message store itself, the record that survives a restart: a delivery is pending
// while its envelope is neither handled nor dead-lettered, or while it is a dead letter marked for replay that the store
// has not yet moved back, and every envelope an Operation's commit wrote has a row.
internal sealed class StoredDeliveries(string connectionString, string schema)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    public string Schema => schema;

    public async Task<HashSet<Guid>> IdsAsync(CancellationToken cancellationToken) =>
        [
            .. (
                await RowsAsync(
                    $"select id from {schema}.wolverine_incoming_envelopes union select id from {schema}.wolverine_dead_letters",
                    cancellationToken
                )
            ).Select(row => (Guid)row[0]),
        ];

    public async Task<List<(Guid Id, Delivery Delivery)>> AllAsync(CancellationToken cancellationToken) =>
        [
            .. (
                await RowsAsync(
                    $"""
                    select id, received_at, message_type, false from {schema}.wolverine_incoming_envelopes
                    union all
                    select id, received_at, message_type, true from {schema}.wolverine_dead_letters
                    order by 1
                    """,
                    cancellationToken
                )
            ).Select(row =>
                ((Guid)row[0], new Delivery(new Uri((string)row[1]).Host, (string)row[2], (bool)row[3]))
            ),
        ];

    /// <summary>
    /// Waits until no delivery is pending: every stored envelope is handled or dead-lettered, and no replay is waiting.
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
                $"Deliveries were still pending after {timeout.TotalSeconds} s: {await PendingAsync(CancellationToken.None)} envelopes in {schema} are neither handled nor dead-lettered, or wait to be replayed."
            );
        }
    }

    public async Task DeleteAllAsync(CancellationToken cancellationToken) =>
        await RowsAsync(
            $"delete from {schema}.wolverine_incoming_envelopes; delete from {schema}.wolverine_outgoing_envelopes; delete from {schema}.wolverine_dead_letters",
            cancellationToken
        );

    private async Task<long> PendingAsync(CancellationToken cancellationToken) =>
        (long)(
            await RowsAsync(
                $"select (select count(*) from {schema}.wolverine_incoming_envelopes where status <> 'Handled') + (select count(*) from {schema}.wolverine_outgoing_envelopes) + (select count(*) from {schema}.wolverine_dead_letters where replayable)",
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
