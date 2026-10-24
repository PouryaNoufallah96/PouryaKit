using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModularMonolith.Tests.Stub;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Email.MailKit;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Persistence.Dapper;
using PouryaKit.Persistence.Postgres;
using PouryaKit.Realtime;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

// A Host whose message store lives in its Module's own schema. It needs a database of its own, because Wolverine
// creates its tables in that schema; the fixture is built in this one test and holds only the Stub, without Modules.
public sealed class SharedStoreSchemaTests
{
    [Fact]
    public async Task A_reset_clears_a_modules_tables_in_the_stores_schema_and_the_store_still_delivers()
    {
        await using var fixture = new SharedSchemaFixture();
        await fixture.InitializeAsync();
        await fixture.WaitForDeliveriesAsync(() => fixture.FileForCopyAsync("before the reset"), TestContext.Current.CancellationToken);
        var before = await fixture.CountsAsync();

        await fixture.ResetAsync(TestContext.Current.CancellationToken);

        // The Module's rows and Receipts are gone with the stored deliveries, and the store still delivers. That the
        // reset leaves the store's own tables alone is not asserted here: a Solo Host keeps no row in them.
        var after = await fixture.CountsAsync();
        var (_, deliveries) = await fixture.WaitForDeliveriesAsync(
            () => fixture.FileForCopyAsync("after the reset"),
            TestContext.Current.CancellationToken
        );
        Assert.Equal((2L, 1L, 1L), before);
        Assert.Equal((0L, 0L, 0L), after);
        Assert.False(Assert.Single(deliveries).DeadLettered);
        Assert.Equal((2L, 1L, 1L), await fixture.CountsAsync());
    }

    private sealed class SharedSchemaFixture : HostFixture
    {
        protected override IHost CreateHost(string connectionString)
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
                ["--environment", "Production", $"--ConnectionStrings:app={connectionString}"]
            );
            builder.Services.UsePouryaKitPostgres<StubDbContext>(connectionName: "app", schema: "stub");
            // The one command it sends, by hand, and what the Stub's Notification Handlers need: its other Features need
            // the Stub Module's services, and the Kit's container is validated when it is built.
            builder.Services.AddScoped<CommandHandler<WriteNoteRaisingCommand>, WriteNoteRaisingCommandHandler>();
            builder.Services.AddSingleton<DeliveryLog>();
            builder.Services.AddPouryaKitMailKit();
            builder.Services.Configure<PouryaKitMailKitOptions>(builder.Configuration.GetSection("PouryaKit:Email"));
            builder.Services.AddPouryaKitRealtime(connectionName: "app");
            builder.Services.AddPouryaKitDapper(connectionName: "app");
            builder.Services.AddPouryaKitNotificationHandlers(typeof(StubDbContext).Assembly);
            builder.UsePouryaKitWolverine(connectionName: "app", schema: "stub");
            builder.Services.UsePouryaKitTestDelivery();
            return builder.Build();
        }

        // A note whose Domain Event the Stub copies: two notes, one Receipt and one stored delivery once delivered.
        public async Task<Result> FileForCopyAsync(string text)
        {
            await using var scope = Host.Services.CreateAsyncScope();
            scope.ServiceProvider.BeginOperation(new OperationContext(Actor.System("shared-schema")));
            return await scope
                .ServiceProvider.GetRequiredService<IDispatcher>()
                .SendAsync(
                    new WriteNoteRaisingCommand(text, [new NoteFiledForCopyDomainEvent(text)]),
                    TestContext.Current.CancellationToken
                );
        }

        public async Task<(long Notes, long Receipts, long Deliveries)> CountsAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(
                """
                select (select count(*) from stub.notes), (select count(*) from stub.pouryakit_receipts),
                       (select count(*) from stub.wolverine_incoming_envelopes)
                """,
                connection
            );
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            await reader.ReadAsync(TestContext.Current.CancellationToken);
            return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        }
    }
}
