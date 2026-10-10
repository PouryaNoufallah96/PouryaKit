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
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        List<object[]> rows = [];
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row);
        }

        return rows;
    }
}
