using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModularMonolith.Tests.Stub;
using Npgsql;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Persistence.EFCore;
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

    // Read as Npgsql reads them: a string that names no database names its user's, and a Unix socket's directory keeps its
    // case and takes the separate port.
    [Theory]
    [InlineData("Host=db;Username=catalog", "Host=db;Username=messages", "db:5432/catalog", "db:5432/messages")]
    [InlineData("Host=/run/postgresql;Database=app", "Host=/run/PostgreSQL;Database=app", "/run/postgresql:5432/app", "/run/PostgreSQL:5432/app")]
    [InlineData("Host=/run/postgresql;Port=5432;Database=app", "Host=/run/postgresql;Port=5433;Database=app", "/run/postgresql:5432/app", "/run/postgresql:5433/app")]
    public void A_dbcontext_on_another_database_as_npgsql_reads_the_strings_fails_the_host_when_built_naming_both(
        string module,
        string store,
        string moduleDatabase,
        string storeDatabase
    )
    {
        var builder = Compose(module, store);

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(
            $"UsePostgres stores StubDbContext over the connection string app, the database {moduleDatabase}, but UsePouryaKitWolverine keeps the message store over the connection string messages, the database {storeDatabase}, so its Unit of Work would write its Outbox to its own database, where the store never delivers it. Use one connection string for the message store and every Module's DbContext.",
            thrown.Message
        );
    }

    // Read as Npgsql reads them: a database named as the other side's user is that user's, and a port inside the host is a
    // number.
    [Theory]
    [InlineData("Host=db;Database=app;Username=catalog", "Host=db;Username=app")]
    [InlineData("Host=db;Port=5432;Database=app", "Host=db:05432;Database=app")]
    public void Two_strings_npgsql_reads_as_one_database_build(string module, string store)
    {
        using var host = Compose(module, store).Build();
    }

    // Npgsql leaves the database of a string that names neither a database nor a user to the client's user, which the
    // string does not show.
    [Fact]
    public void A_dbcontext_whose_string_names_neither_a_database_nor_a_user_fails_the_host_when_built_naming_its_connection()
    {
        var builder = Compose("Host=db", "Host=db;Database=app");

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(
            "UsePostgres stores StubDbContext over the connection string app, which names neither a database nor a user, so its database is left to the client's user and cannot be compared with the message store's, the database db:5432/app. Set Database, or at least Username, in the connection string app.",
            thrown.Message
        );
    }

    [Fact]
    public void A_message_store_whose_string_names_neither_a_database_nor_a_user_fails_the_host_when_built_naming_its_connection()
    {
        var builder = Compose("Host=db;Database=app", "Host=db");

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(
            "UsePouryaKitWolverine keeps the message store over the connection string messages, which names neither a database nor a user, so its database is left to the client's user and no DbContext's can be compared with it. Set Database, or at least Username, in the connection string messages.",
            thrown.Message
        );
    }

    // EF Core applies a ConfigureDbContext after AddDbContext over it, so the DbContext uses the string it names.
    [Fact]
    public void A_dbcontext_that_configure_db_context_moves_to_another_database_fails_the_host_when_built_naming_it()
    {
        var builder = Compose("Host=db;Database=app", "Host=db;Database=app");
        builder.Services.ConfigureDbContext<StubDbContext>(options => options.UseNpgsql("Host=db;Database=elsewhere"));

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(
            "UsePostgres stores StubDbContext over the connection string app, the database db:5432/elsewhere, but UsePouryaKitWolverine keeps the message store over the connection string messages, the database db:5432/app, so its Unit of Work would write its Outbox to its own database, where the store never delivers it. Use one connection string for the message store and every Module's DbContext.",
            thrown.Message
        );
    }

    // EF Core gives a DbContext moved onto an Npgsql data source the source's connection, whether the string UsePostgres
    // wrote was cleared first or left in place, unused.
    [Fact]
    public void A_dbcontext_moved_onto_a_data_source_on_another_database_fails_the_host_when_built_naming_it()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=db;Database=elsewhere;Username=catalog");
        var builder = Compose("Host=db;Database=app", "Host=db;Database=app");
        builder.Services.ConfigureDbContext<StubDbContext>(options => options.UseNpgsql((string?)null).UseNpgsql(dataSource));

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(
            "UsePostgres stores StubDbContext over the connection string app, the database db:5432/elsewhere, but UsePouryaKitWolverine keeps the message store over the connection string messages, the database db:5432/app, so its Unit of Work would write its Outbox to its own database, where the store never delivers it. Use one connection string for the message store and every Module's DbContext.",
            thrown.Message
        );
    }

    [Fact]
    public void A_dbcontext_moved_onto_a_data_source_on_the_message_stores_database_builds_whatever_string_it_left()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=db;Database=app;Username=catalog");
        var builder = Compose("Host=db;Database=elsewhere", "Host=db;Database=app");
        builder.Services.ConfigureDbContext<StubDbContext>(options => options.UseNpgsql(dataSource));

        using var host = builder.Build();
    }

    // Constructing a DbContext builds its connection and its model, so what EF Core refuses there fails the build as EF
    // Core's own exception, which names the cause: here a ConfigureDbContext onto a name the configuration lacks.
    [Fact]
    public void A_dbcontext_on_a_named_string_the_configuration_lacks_fails_the_host_when_built_with_ef_cores_exception()
    {
        var builder = Compose("Host=db;Database=app", "Host=db;Database=app");
        builder.Services.ConfigureDbContext<StubDbContext>(options => options.UseNpgsql("Name=ConnectionStrings:missing"));

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.StartsWith(
            "A named connection string was used, but the name 'ConnectionStrings:missing' was not found in the application's configuration.",
            thrown.Message
        );
    }

    [Fact]
    public void A_dbcontext_whose_model_ef_core_refuses_fails_the_host_when_built_with_ef_cores_exception()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            ["--environment", "Production", "--ConnectionStrings:app=Host=db;Database=app"]
        );
        builder.Services.UsePostgres<KeylessModelContext>(connectionName: "app", schema: "keyless");
        builder.UsePouryaKitWolverine(connectionName: "app");

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.StartsWith("The entity type 'Keyless' requires a primary key to be defined.", thrown.Message);
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

    // AddPouryaKitEfCore attaches the Outbox to a DbContext the application registered itself, on a connection no
    // configuration name gives (final review, finding 2).
    [Fact]
    public void A_dbcontext_the_application_registered_on_another_database_fails_the_host_when_built_naming_it()
    {
        var module = new NpgsqlConnectionStringBuilder(app.ConnectionString) { Database = "module" };
        var store = new NpgsqlConnectionStringBuilder(app.ConnectionString);
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            ["--environment", "Production", $"--ConnectionStrings:messages={store.ConnectionString}"]
        );
        builder.Services.AddDbContext<StubDbContext>(options => options.UseNpgsql(module.ConnectionString));
        builder.Services.AddPouryaKitEfCore<StubDbContext>();
        builder.UsePouryaKitWolverine(connectionName: "messages");

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(
            $"AddPouryaKitEfCore makes StubDbContext a Unit of Work over the connection its own registration gives it, the database {module.Host}:{module.Port}/module, but UsePouryaKitWolverine keeps the message store over the connection string messages, the database {store.Host}:{store.Port}/{store.Database}, so its Unit of Work would write its Outbox to its own database, where the store never delivers it. Register StubDbContext on the message store's database.",
            thrown.Message
        );
    }

    // A name AddPouryaKitEfCore records is checked without UsePostgres too, and the store's check leaves it to that line
    // (final review D, F1).
    [Fact]
    public void A_connection_name_add_pouryakit_ef_core_records_and_the_configuration_lacks_fails_the_host_when_built_naming_its_key()
    {
        var module = new NpgsqlConnectionStringBuilder(app.ConnectionString) { Database = "module" };
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            ["--environment", "Production", $"--ConnectionStrings:messages={app.ConnectionString}"]
        );
        builder.Services.AddDbContext<StubDbContext>(options => options.UseNpgsql(module.ConnectionString));
        builder.Services.AddPouryaKitEfCore<StubDbContext>("unset");
        builder.UsePouryaKitWolverine(connectionName: "messages");

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(
            "StubDbContext stores over the connection string unset, which the Host's configuration does not set (ConnectionStrings:unset), so every Operation that opens it would fail. Set ConnectionStrings:unset, or name a connection the Host configures where StubDbContext is registered.",
            thrown.Message
        );
    }

    // A DbContext the application registered itself names no connection string, so the check of named ones passes it by.
    [Fact]
    public void A_dbcontext_the_application_registered_on_the_message_stores_database_builds_beside_use_postgres()
    {
        var builder = Compose(app.ConnectionString, app.ConnectionString);
        builder.Services.AddDbContext<ReadCollectionDbContext>(options => options.UseNpgsql(app.ConnectionString));
        builder.Services.AddPouryaKitEfCore<ReadCollectionDbContext>();

        using var host = builder.Build();
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

internal sealed class Keyless
{
    public string Name { get; private set; } = "";
}

internal sealed class KeylessModelContext(DbContextOptions<KeylessModelContext> options) : PouryaKitDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Keyless>();
    }
}
