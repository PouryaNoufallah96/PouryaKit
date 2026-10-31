using Npgsql;

namespace ModularMonolith.Tests.Fixtures;

/// <summary>What a test reads from a database directly, beside the Host that writes it.</summary>
public static class Sql
{
    /// <summary>Every row <paramref name="sql"/> returns on <paramref name="connectionString"/>, read on a connection of its own.</summary>
    public static async Task<List<object[]>> RowsAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken
        );
        List<object[]> rows = [];
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// Waits until the SQL <paramref name="condition"/> holds on <paramref name="connectionString"/>, read every 25 ms;
    /// fails with <paramref name="because"/>, or with the condition when none is given, after <paramref name="seconds"/>.
    /// </summary>
    public static async Task WaitUntilAsync(
        string connectionString,
        string condition,
        string? because = null,
        int seconds = 15
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        while (!(bool)(await RowsAsync(connectionString, $"select {condition}"))[0][0])
        {
            Assert.False(
                timeout.IsCancellationRequested,
                because ?? $"Not reached within {seconds} s: {condition}"
            );
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }
}
