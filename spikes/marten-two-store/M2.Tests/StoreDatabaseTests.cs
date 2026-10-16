using M2.Host;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wolverine.Persistence;

namespace M2.Tests;

// Item 14: Wolverine's Marten outbox writes each envelope on the Marten session's connection into the message store's
// tables, so Marten's store must be on the message store's database. The Marten side of the Kit's message-store
// database check fails the Host when it is built; without it, the two main stores fail it only at start.
public sealed class StoreDatabaseTests(Postgres postgres) : SpikeTest(postgres)
{
    [Fact]
    public async Task A_marten_store_on_another_database_than_the_message_store_fails_the_host_when_it_is_built()
    {
        var other = await CreateDatabaseAsync();

        var refused = Assert.ThrowsAny<Exception>(() => Build(OnAnotherDatabase(other, check: true)));

        Assert.Contains($"Marten's store is the database {new NpgsqlConnectionStringBuilder(other).Database} of the connection string marten", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_check_the_two_databases_fail_the_host_only_at_start()
    {
        var other = await CreateDatabaseAsync();
        var host = Build(OnAnotherDatabase(other, check: false));

        var refused = await Assert.ThrowsAsync<InvalidWolverineStorageConfigurationException>(() => host.StartAsync(Token));

        Assert.Contains("exactly one message store tagged as the 'main' store", refused.Message, StringComparison.Ordinal);
    }

    private static SpikeHostOptions OnAnotherDatabase(string connectionString, bool check) =>
        new()
        {
            MartenConnectionName = "marten",
            CheckMartenDatabase = check,
            Configuration = new() { ["ConnectionStrings:marten"] = connectionString },
        };
}
