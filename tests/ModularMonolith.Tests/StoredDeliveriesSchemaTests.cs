using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using Npgsql;
using PouryaKit.Caching;
using PouryaKit.Cqrs;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Persistence.EFCore;
using PouryaKit.Persistence.Postgres;
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
            await SqlAsync(
                $"""
                create schema {quotedSchema};
                create table {quotedSchema}.wolverine_incoming_envelopes
                    (id uuid, received_at text, message_type text, status text, attempts int not null default 0, execution_time timestamptz);
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
                """
            );

            var type = typeof(HostFixture).Assembly.GetType(
                "PouryaKit.Testing.StoredDeliveries",
                throwOnError: true
            )!;
            var stored = Activator.CreateInstance(
                type,
                Fixture.ConnectionString,
                schema,
                Array.Empty<(string, string)>()
            )!;

            switch (method)
            {
                case "IdsAsync":
                    var ids = await InvokeAsync<HashSet<Guid>>(method);
                    Assert.True(ids.SetEquals([incoming, pendingIncoming, deadLetter, replay]));
                    break;
                case "AllAsync":
                    var all = await InvokeAsync<List<(Guid Id, Delivery Delivery)>>(method);
                    Assert.Equal(4, all.Count);
                    Assert.Contains(
                        (incoming, new Delivery("tests.handled", "tests.incoming.v1", false)),
                        all
                    );
                    Assert.Contains(
                        (
                            pendingIncoming,
                            new Delivery("tests.pending", "tests.incoming.v1", false)
                        ),
                        all
                    );
                    Assert.Contains(
                        (deadLetter, new Delivery("tests.dead", "tests.dead.v1", true)),
                        all
                    );
                    Assert.Contains(
                        (replay, new Delivery("tests.replay", "tests.dead.v1", true)),
                        all
                    );
                    break;
                case "PendingAsync":
                    Assert.Equal(3L, await InvokeAsync<long>(method));
                    await SqlAsync(
                        $"update {quotedSchema}.wolverine_dead_letters set replayable = false"
                    );
                    Assert.Equal(2L, await InvokeAsync<long>(method));
                    await SqlAsync($"delete from {quotedSchema}.wolverine_outgoing_envelopes");
                    Assert.Equal(1L, await InvokeAsync<long>(method));
                    await SqlAsync(
                        $"update {quotedSchema}.wolverine_incoming_envelopes set status = 'Handled'"
                    );
                    Assert.Equal(0L, await InvokeAsync<long>(method));
                    break;
                case "DeleteAllAsync":
                    await (Task)type.GetMethod(method)!.Invoke(stored, [cancellationToken])!;
                    await using (
                        var count = new NpgsqlCommand(
                            $"""
                            select (select count(*) from {quotedSchema}.wolverine_incoming_envelopes)
                                + (select count(*) from {quotedSchema}.wolverine_outgoing_envelopes)
                                + (select count(*) from {quotedSchema}.wolverine_dead_letters)
                            """,
                            connection
                        )
                    )
                    {
                        Assert.Equal(0L, await count.ExecuteScalarAsync(cancellationToken));
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(method));
            }

            async Task<T> InvokeAsync<T>(string name) =>
                await (Task<T>)
                    type.GetMethod(
                                name,
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                            )!
                        .Invoke(stored, [cancellationToken])!;
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand(
                $"drop schema if exists {quotedSchema} cascade",
                connection
            );
            await cleanup.ExecuteNonQueryAsync(CancellationToken.None);
        }

        async Task SqlAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task A_delivery_received_at_a_broker_queue_before_it_reached_a_subscription_is_named_by_that_address()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(Fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        Guid received = Guid.CreateVersion7(),
            copy = Guid.CreateVersion7(),
            deadLetter = Guid.CreateVersion7();
        try
        {
            await using (
                var setup = new NpgsqlCommand(
                    $"""
                    create schema "review-broker";
                    create table "review-broker".wolverine_incoming_envelopes (id uuid, received_at text, message_type text);
                    create table "review-broker".wolverine_dead_letters (id uuid, received_at text, message_type text);
                    insert into "review-broker".wolverine_incoming_envelopes values
                        ('{received}', 'rabbitmq://queue/inventory', 'tests.incoming.v1'),
                        ('{copy}', 'local://tests.handled/', 'tests.incoming.v1');
                    insert into "review-broker".wolverine_dead_letters values ('{deadLetter}', 'rabbitmq://queue/inventory', 'tests.retired.v1');
                    """,
                    connection
                )
            )
            {
                await setup.ExecuteNonQueryAsync(cancellationToken);
            }

            var type = typeof(HostFixture).Assembly.GetType(
                "PouryaKit.Testing.StoredDeliveries",
                throwOnError: true
            )!;
            var stored = Activator.CreateInstance(
                type,
                Fixture.ConnectionString,
                "review-broker",
                Array.Empty<(string, string)>()
            )!;
            var all = await (Task<List<(Guid Id, Delivery Delivery)>>)
                type.GetMethod("AllAsync")!.Invoke(stored, [cancellationToken])!;

            Assert.Equal(3, all.Count);
            Assert.Contains(
                (received, new Delivery("rabbitmq://queue/inventory", "tests.incoming.v1", false)),
                all
            );
            Assert.Contains((copy, new Delivery("tests.handled", "tests.incoming.v1", false)), all);
            Assert.Contains(
                (deadLetter, new Delivery("rabbitmq://queue/inventory", "tests.retired.v1", true)),
                all
            );
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand(
                "drop schema if exists \"review-broker\" cascade",
                connection
            );
            await cleanup.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Receipts_and_snapshot_versions_write_in_a_schema_containing_a_quote(
        bool snapshot
    )
    {
        const string schema = "review\"quoted";
        var cancellationToken = TestContext.Current.CancellationToken;
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder([
            "--environment",
            "Production",
            $"--ConnectionStrings:app={Fixture.ConnectionString}",
        ]);
        builder.Services.UsePouryaKitPostgres<QuotedSchemaDbContext>("app", schema);
        builder.Services.AddPouryaKitCqrs(typeof(StubModule).Assembly);
        builder.Services.AddPouryaKitCaching();
        builder.Services.AddScoped(
            typeof(IReceipts),
            typeof(ReceiptTable).Assembly.GetType(
                "PouryaKit.Messaging.Wolverine.EFCore.EfReceipts",
                throwOnError: true
            )!
        );
        using var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(new OperationContext(Actor.System("review")));
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotedSchemaDbContext>();
        var migration = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        migration.EnsureSchema(schema);
        migration.CreateReceiptTable(schema);
        migration.CreateSnapshotVersionTable(schema);
        try
        {
            foreach (
                var command in dbContext
                    .GetService<IMigrationsSqlGenerator>()
                    .Generate(migration.Operations, dbContext.Model)
            )
            {
                await dbContext.Database.ExecuteSqlRawAsync(command.CommandText, cancellationToken);
            }

            var receipts = scope.ServiceProvider.GetRequiredService<IReceipts>();
            var messageId = Guid.CreateVersion7();
            await using (
                var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken)
            )
            {
                if (snapshot)
                {
                    Assert.True(
                        await receipts.RecordVersionAsync(
                            "tests.review",
                            "aggregate",
                            1,
                            cancellationToken
                        )
                    );
                    Assert.True(
                        await receipts.RecordVersionAsync(
                            "tests.review",
                            "aggregate",
                            2,
                            cancellationToken
                        )
                    );
                    Assert.False(
                        await receipts.RecordVersionAsync(
                            "tests.review",
                            "aggregate",
                            1,
                            cancellationToken
                        )
                    );
                }
                else
                {
                    Assert.True(
                        await receipts.RecordAsync("tests.review", messageId, cancellationToken)
                    );
                    Assert.False(
                        await receipts.RecordAsync("tests.review", messageId, cancellationToken)
                    );
                }

                await transaction.CommitAsync(cancellationToken);
            }

            await using var connection = new NpgsqlConnection(Fixture.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var stored = new NpgsqlCommand(
                snapshot
                    ? "select version from \"review\"\"quoted\".pouryakit_snapshot_versions where subscription = 'tests.review' and aggregate_id = 'aggregate'"
                    : "select count(*) from \"review\"\"quoted\".pouryakit_receipts where subscription = 'tests.review' and message_id = @id",
                connection
            );
            if (!snapshot)
            {
                stored.Parameters.AddWithValue("id", messageId);
            }

            Assert.Equal(snapshot ? 2L : 1L, await stored.ExecuteScalarAsync(cancellationToken));
        }
        finally
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "drop schema if exists \"review\"\"quoted\" cascade",
                CancellationToken.None
            );
        }
    }

    private sealed class QuotedSchemaDbContext(DbContextOptions<QuotedSchemaDbContext> options)
        : PouryaKitDbContext(options)
    {
        // Only the migration-created messaging tables are needed; the Stub's mappings belong to its own DbContext.
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.HasDefaultSchema("review\"quoted");
    }
}
