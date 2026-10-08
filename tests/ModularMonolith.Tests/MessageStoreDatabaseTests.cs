using Microsoft.Extensions.Hosting;
using ModularMonolith.Tests.Stub;
using Npgsql;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Persistence.Postgres;

namespace ModularMonolith.Tests;

// The message store and every DbContext share one database, checked when the Host is built (ticket #63), in the composition
// of #54's two-database probe (commit 3861dc9): the Stub's DbContext as the application's Unit of Work on the connection
// app, the store on the connection messages, on the fixture's PostgreSQL. Nothing connects while the Host is built, so
// neither database has to exist.
public sealed class MessageStoreDatabaseTests(AppFixture app)
{
    // The probe's shape, another database on the store's server; then the store's database on another port or host.
    [Theory]
    [InlineData("store", null, null)]
    [InlineData(null, 1, null)]
    [InlineData(null, null, "store.invalid")]
    public void A_dbcontext_on_another_database_than_the_message_store_fails_the_host_when_built_naming_both(
        string? storeDatabase,
        int? portOffset,
        string? storeHost
    )
    {
        var module = new NpgsqlConnectionStringBuilder(app.ConnectionString) { Database = "module" };
        var store = new NpgsqlConnectionStringBuilder(module.ConnectionString);
        store.Database = storeDatabase ?? store.Database;
        store.Port += portOffset ?? 0;
        store.Host = storeHost ?? store.Host;
        var builder = Compose(module.ConnectionString, store.ConnectionString);

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(
            $"UsePostgres stores StubDbContext over the connection string app, the database {module.Host}:{module.Port}/module, but UsePouryaKitWolverine keeps the message store over the connection string messages, the database {store.Host}:{store.Port}/{store.Database}, so its Unit of Work would write its Outbox to its own database, where the store never delivers it. Use one connection string for the message store and every Module's DbContext.",
            thrown.Message
        );
    }

    // The store's string names the fixture's database in other words: Server for Host, the port inside the host, the host
    // in upper case, and a setting that leaves the database alone.
    [Fact]
    public void A_dbcontext_and_the_message_store_on_one_database_under_two_connection_names_build()
    {
        var database = new NpgsqlConnectionStringBuilder(app.ConnectionString);
        var store =
            $"Server={database.Host!.ToUpperInvariant()}:{database.Port};Database={database.Database};User ID={database.Username};Password={database.Password};Application Name=message-store";

        using var host = Compose(app.ConnectionString, store).Build();
    }

    private static HostApplicationBuilder Compose(string module, string store)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            ["--environment", "Production", $"--ConnectionStrings:app={module}", $"--ConnectionStrings:messages={store}"]
        );
        builder.Services.UsePostgres<StubDbContext>(connectionName: "app", schema: "stub");
        builder.UsePouryaKitWolverine(connectionName: "messages");
        return builder;
    }
}
