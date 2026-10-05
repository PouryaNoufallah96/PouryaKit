using Microsoft.Extensions.Hosting;
using ModularMonolith.Host;
using ModularMonolith.Tests;
using ModularMonolith.Tests.Stub;
using Npgsql;
using PouryaKit.Modularity;
using PouryaKit.Testing;

[assembly: AssemblyFixture(typeof(AppFixture))]
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

namespace ModularMonolith.Tests;

/// <summary>The Proving Sample's Host, with the test-only <see cref="StubModule"/> beside Catalog.</summary>
public sealed class AppFixture : HostFixture
{
    protected override IHost CreateHost(string connectionString)
    {
        var builder = ModularMonolithHost.CreateBuilder(
            ["--environment", "Development", $"--ConnectionStrings:app={connectionString}"]
        );
        builder.AddPouryaKitModules(modules => modules.Add<StubModule>());
        return builder.Build();
    }

    /// <summary>Every row <paramref name="sql"/> returns, read on a connection of its own.</summary>
    public async Task<List<object[]>> RowsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
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

    public async Task<T> ScalarAsync<T>(string sql) => (T)(await RowsAsync(sql))[0][0];
}
