using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModularMonolith.Tests.Stub;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Persistence.Dapper;
using PouryaKit.Persistence.Postgres;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

// A probe of ticket #54, recorded in docs/acceptance.md and then removed: it asserts nothing and writes what it observes.
// The Stub's DbContext stores on the connection app and the message store on the connection messages, two databases of
// the fixture's PostgreSQL. One command writes a note whose Domain Event a Notification Handler publishes an Integration
// Event for, which the Host subscribes to; another writes a note and publishes nothing. With staleStoreSchema, a Host
// that used the Module's database as its store ran there first, leaving the store's tables behind.
public sealed class MessageStoreDatabaseTests(AppFixture app)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Probe_a_module_and_the_message_store_on_two_databases(bool staleStoreSchema)
    {
        var output = TestContext.Current.TestOutputHelper!;
        var module = await NewDatabaseAsync("module");
        var store = await NewDatabaseAsync("store");
        if (staleStoreSchema)
        {
            using var earlier = Compose(module, module, new RecordedLogs());
            await HostFixture.StartHostAsync(earlier, TestContext.Current.CancellationToken);
            await earlier.StopAsync(TestContext.Current.CancellationToken);
        }

        var logs = new RecordedLogs();
        using var host = Compose(module, store, logs);
        output.WriteLine("built");
        await HostFixture.StartHostAsync(host, TestContext.Current.CancellationToken);
        output.WriteLine("started");
        try
        {
            try
            {
                var shared = await SendAsync(host, new WriteNoteRaisingCommand("shared", [new NoteToShareDomainEvent("shared")]));
                output.WriteLine($"shared: success {shared.IsSuccess}");
            }
            catch (Exception exception)
            {
                output.WriteLine($"shared: threw {exception.GetType().FullName}: {exception.Message}");
                output.WriteLine(exception.StackTrace ?? "");
            }

            var plain = await SendAsync(host, new WriteNoteRaisingCommand("plain", []));
            output.WriteLine($"plain: success {plain.IsSuccess}");
            await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            const string Schemas = "select nspname from pg_namespace where nspname not like 'pg_%' and nspname <> 'information_schema' order by 1";
            await DumpAsync(output, "module", module, Schemas);
            await DumpAsync(output, "module", module, "select text from stub.notes order by id");
            await DumpAsync(output, "module", module, "select subscription, message_id from stub.pouryakit_receipts");
            await DumpAsync(output, "module", module, "select id, status, owner_id, received_at, message_type from wolverine.wolverine_incoming_envelopes");
            await DumpAsync(output, "store", store, Schemas);
            await DumpAsync(output, "store", store, "select id, status, owner_id, received_at, message_type from wolverine.wolverine_incoming_envelopes");
            await DumpAsync(output, "store", store, "select id, destination, message_type from wolverine.wolverine_outgoing_envelopes");
            await DumpAsync(output, "store", store, "select id, exception_type, exception_message from wolverine.wolverine_dead_letters");
            output.WriteLine($"Notification Handler deliveries: {host.Services.GetRequiredService<DeliveryLog>().For("shared").Count}");
            output.WriteLine($"Integration Event Handler deliveries: {host.Services.GetRequiredService<IntegrationEventLog>().All.Count}");
            foreach (var entry in logs.Entries.Where(entry => entry.Level >= LogLevel.Warning))
            {
                output.WriteLine($"log {entry.Level} {entry.Category}: {entry.Message} {entry.Exception?.GetType().FullName}");
            }
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    // Without Modules, as an application of one composes them: the Stub's DbContext is its Unit of Work.
    private static IHost Compose(string module, string store, RecordedLogs logs)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            ["--environment", "Production", $"--ConnectionStrings:app={module}", $"--ConnectionStrings:messages={store}"]
        );
        builder.Services.UsePostgres<StubDbContext>(connectionName: "app", schema: "stub");
        builder.Services.AddScoped<CommandHandler<WriteNoteRaisingCommand>, WriteNoteRaisingCommandHandler>();
        builder.Services.AddSingleton<DeliveryLog>();
        builder.Services.AddSingleton<IntegrationEventLog>();
        builder.Services.AddPouryaKitDapper(connectionName: "app");
        builder.Services.AddPouryaKitNotificationHandlers(typeof(StubDbContext).Assembly);
        builder.Services.Publish<NoteSharedV1>("stub.note-shared.v1");
        builder.Services.Subscribe<NoteSharedV1, RecordSharedNoteIntegrationEventHandler>("stub.note-shared");
        builder.UsePouryaKitWolverine(connectionName: "messages");
        builder.Services.UsePouryaKitTestDelivery();
        builder.Logging.AddProvider(logs);
        return builder.Build();
    }

    private static async Task<Result> SendAsync(IHost host, Command command)
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(new OperationContext(Actor.System("probe")));
        return await scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync(command, TestContext.Current.CancellationToken);
    }

    private static async Task DumpAsync(ITestOutputHelper output, string database, string connectionString, string sql)
    {
        output.WriteLine($"[{database}] {sql}");
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                var row = new object[reader.FieldCount];
                reader.GetValues(row);
                output.WriteLine($"    {string.Join(" | ", row)}");
            }
        }
        catch (PostgresException exception)
        {
            output.WriteLine($"    {exception.SqlState}: {exception.MessageText}");
        }
    }

    private async Task<string> NewDatabaseAsync(string name)
    {
        var database = $"{name}_{Guid.CreateVersion7():N}";
        await app.RowsAsync($"create database {database}");
        return new NpgsqlConnectionStringBuilder(app.ConnectionString) { Database = database }.ConnectionString;
    }
}
