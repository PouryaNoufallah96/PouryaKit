using System.Reflection;
using Npgsql;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

public sealed class StoredDeliveriesSchemaTests(AppFixture app) : HostTest<AppFixture>(app)
{
    [Theory]
    [InlineData("review-store", "IdsAsync")]
    [InlineData("review-store\"quoted", "IdsAsync")]
    [InlineData("review-store", "AllAsync")]
    [InlineData("review-store\"quoted", "AllAsync")]
    [InlineData("review-store", "PendingAsync")]
    [InlineData("review-store\"quoted", "PendingAsync")]
    [InlineData("review-store", "DeleteAllAsync")]
    [InlineData("review-store\"quoted", "DeleteAllAsync")]
    public async Task Message_store_queries_use_the_exact_schema_name(string schema, string method)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var sqlBuilder = new NpgsqlCommandBuilder();
        var quotedSchema = sqlBuilder.QuoteIdentifier(schema);
        await using var connection = new NpgsqlConnection(Fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var incoming = Guid.CreateVersion7();
        var pendingIncoming = Guid.CreateVersion7();
        var deadLetter = Guid.CreateVersion7();
        var replay = Guid.CreateVersion7();
        var outgoing = Guid.CreateVersion7();
        // Minimal store tables isolate the reader from Wolverine's schema provisioning.
        try
        {
            await SqlAsync($"""
                create schema {quotedSchema};
                create table {quotedSchema}.wolverine_incoming_envelopes
                    (id uuid, received_at text, message_type text, status text);
                create table {quotedSchema}.wolverine_outgoing_envelopes (id uuid);
                create table {quotedSchema}.wolverine_dead_letters
                    (id uuid, received_at text, message_type text, replayable bool);
                insert into {quotedSchema}.wolverine_incoming_envelopes values
                    ('{incoming}', 'local://tests.handled/', 'tests.incoming.v1', 'Handled'),
                    ('{pendingIncoming}', 'local://tests.pending/', 'tests.incoming.v1', 'Incoming');
                insert into {quotedSchema}.wolverine_dead_letters values
                    ('{deadLetter}', 'local://tests.dead/', 'tests.dead.v1', false),
                    ('{replay}', 'local://tests.replay/', 'tests.dead.v1', true);
                insert into {quotedSchema}.wolverine_outgoing_envelopes values ('{outgoing}');
                """);

            var type = typeof(HostFixture).Assembly.GetType("PouryaKit.Testing.StoredDeliveries", throwOnError: true)!;
            var stored = Activator.CreateInstance(type, Fixture.ConnectionString, schema, Array.Empty<(string, string)>())!;

            switch (method)
            {
                case "IdsAsync":
                    var ids = await InvokeAsync<HashSet<Guid>>(method);
                    Assert.True(ids.SetEquals([incoming, pendingIncoming, deadLetter, replay]));
                    break;
                case "AllAsync":
                    var all = await InvokeAsync<List<(Guid Id, Delivery Delivery)>>(method);
                    Assert.Equal(4, all.Count);
                    Assert.Contains((incoming, new Delivery("tests.handled", "tests.incoming.v1", false)), all);
                    Assert.Contains((pendingIncoming, new Delivery("tests.pending", "tests.incoming.v1", false)), all);
                    Assert.Contains((deadLetter, new Delivery("tests.dead", "tests.dead.v1", true)), all);
                    Assert.Contains((replay, new Delivery("tests.replay", "tests.dead.v1", true)), all);
                    break;
                case "PendingAsync":
                    Assert.Equal(3L, await InvokeAsync<long>(method));
                    await SqlAsync($"update {quotedSchema}.wolverine_dead_letters set replayable = false");
                    Assert.Equal(2L, await InvokeAsync<long>(method));
                    await SqlAsync($"delete from {quotedSchema}.wolverine_outgoing_envelopes");
                    Assert.Equal(1L, await InvokeAsync<long>(method));
                    await SqlAsync($"update {quotedSchema}.wolverine_incoming_envelopes set status = 'Handled'");
                    Assert.Equal(0L, await InvokeAsync<long>(method));
                    break;
                case "DeleteAllAsync":
                    await (Task)type.GetMethod(method)!.Invoke(stored, [cancellationToken])!;
                    await using (var count = new NpgsqlCommand($"""
                        select (select count(*) from {quotedSchema}.wolverine_incoming_envelopes)
                            + (select count(*) from {quotedSchema}.wolverine_outgoing_envelopes)
                            + (select count(*) from {quotedSchema}.wolverine_dead_letters)
                        """, connection))
                    {
                        Assert.Equal(0L, await count.ExecuteScalarAsync(cancellationToken));
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(method));
            }

            async Task<T> InvokeAsync<T>(string name) =>
                await (Task<T>)type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(stored, [cancellationToken])!;
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($"drop schema if exists {quotedSchema} cascade", connection);
            await cleanup.ExecuteNonQueryAsync(CancellationToken.None);
        }

        async Task SqlAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
