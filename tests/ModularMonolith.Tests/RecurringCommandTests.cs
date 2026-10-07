using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Testing;
using Wolverine;
using Wolverine.Runtime;

namespace ModularMonolith.Tests;

/// <summary>
/// Inventory's ReconcileStock is recurring work: each occurrence of its cadence runs as an Operation of its own for the
/// system Actor its Composition Root names, and stores the next occurrence in its commit, so whichever Host runs next
/// goes on with it. Each test runs its Hosts on a database of their own, at a short cadence, so the fixture's Host never
/// runs an occurrence; and observes what the occurrences committed, never how often the transport ran a handler.
/// </summary>
public sealed class RecurringCommandTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private const string ReconcileStock = "inventory.reconcile-stock";
    private const string Reconciler = "inventory.stock-reconciler";

    [Fact]
    public async Task ReconcileStock_runs_at_each_occurrence_under_its_system_actor_and_goes_on_after_a_host_restart()
    {
        var connectionString = await NewDatabaseAsync();
        var turnedAway = DeliveryProbes.TurnedAwayFrom(ReconcileStock);
        await using var dying = Compose(connectionString, TimeSpan.FromSeconds(1));
        await using var starting = Compose(connectionString, TimeSpan.FromSeconds(1));
        await HostFixture.StartHostAsync(dying, TestContext.Current.CancellationToken);
        await WaitUntilAsync(connectionString, "(select count(*) from inventory.stock_reconciliations) >= 2");
        // Wolverine's quick stop neither drains nor gives up what the Host holds, as a killed process leaves it.
        ((WolverineRuntime)dying.Services.GetRequiredService<IWolverineRuntime>()).StopMode = StopMode.Quick;
        await dying.StopAsync(TestContext.Current.CancellationToken);
        var stored = await RowsAsync(connectionString, Waiting);

        try
        {
            await HostFixture.StartHostAsync(starting, TestContext.Current.CancellationToken);
            var restarted = (DateTime)(await RowsAsync(connectionString, "select now()"))[0][0];
            // Two, so at least one is the starting Host's, whatever the dying one still had running.
            await WaitUntilAsync(
                connectionString,
                $"(select count(*) from inventory.stock_reconciliations where pg_xact_commit_timestamp(xmin) > '{restarted:O}') >= 2"
            );
        }
        finally
        {
            await starting.StopAsync(TestContext.Current.CancellationToken);
        }

        // The next occurrence was stored when the Host died, and the starting Host went on with it and added none.
        Assert.Equal([["Scheduled"]], stored);
        Assert.Equal(turnedAway, DeliveryProbes.TurnedAwayFrom(ReconcileStock));
        Assert.Single(await RowsAsync(connectionString, Waiting));
        // One reconciliation per occurrence, each on a whole second of the cadence, by the declared system Actor, with
        // its Receipt.
        var reconciliations = await RowsAsync(
            connectionString,
            "select count(*), count(distinct occurrence), bool_and(date_trunc('second', occurrence) = occurrence), bool_and(reconciled_by = '" + Reconciler + "') from inventory.stock_reconciliations"
        );
        Assert.True((long)reconciliations[0][0] >= 4);
        Assert.Equal([reconciliations[0][0], reconciliations[0][0], true, true], reconciliations[0]);
        Assert.Equal(
            reconciliations[0][0],
            (await RowsAsync(connectionString, $"select count(*) from inventory.pouryakit_receipts where subscription = '{ReconcileStock}'"))[0][0]
        );
        Assert.Empty(await RowsAsync(connectionString, "select id from wolverine.wolverine_dead_letters"));
        // Each attempt was a new Operation of the system Actor, starting a chain of its own: continuing nothing, trusted
        // as nothing's continuation, and for no tenant.
        var contexts = DeliveryProbes.ContextsOf(ReconcileStock);
        Assert.NotEmpty(contexts);
        Assert.All(
            contexts,
            context => Assert.Equal(
                (Actor.System(Reconciler), (string?)null, context.OperationId, (Guid?)null, false),
                (context.Actor, context.Tenant, context.CorrelationId, context.CausationId, context.IsTrustedContinuation)
            )
        );
    }

    [Fact]
    public async Task An_occurrence_delivered_again_finds_its_receipt_and_neither_reconciles_again_nor_stores_another_occurrence()
    {
        var connectionString = await NewDatabaseAsync();
        await using var host = Compose(connectionString, TimeSpan.FromSeconds(3));
        await HostFixture.StartHostAsync(host, TestContext.Current.CancellationToken);
        try
        {
            await WaitUntilAsync(connectionString, $"exists (select from wolverine.wolverine_incoming_envelopes where {OnQueue} and status = 'Handled')");
            var turnedAway = DeliveryProbes.TurnedAwayFrom(ReconcileStock);
            // The next occurrence was stored in the commit of the first one's reconciliation, and waits.
            Assert.Equal(
                [["Scheduled", true]],
                await RowsAsync(
                    connectionString,
                    $"select e.status, pg_xact_commit_timestamp(e.xmin) = pg_xact_commit_timestamp(r.xmin) from wolverine.wolverine_incoming_envelopes e, inventory.stock_reconciliations r where e.{OnQueue} and e.status <> 'Handled'"
                )
            );

            // Pending again, as after a Host that died before acknowledging it, or a replay.
            Assert.Single(
                await RowsAsync(
                    connectionString,
                    $"update wolverine.wolverine_incoming_envelopes set status = 'Incoming', owner_id = 0 where {OnQueue} and status = 'Handled' returning id"
                )
            );
            await WaitUntilAsync(connectionString, $"not exists (select from wolverine.wolverine_incoming_envelopes where {OnQueue} and status = 'Incoming')");

            // Turned away well before the next occurrence, three seconds on, is due: nothing more stored for it.
            Assert.Equal(turnedAway + 1, DeliveryProbes.TurnedAwayFrom(ReconcileStock));
            Assert.Single(await RowsAsync(connectionString, Waiting));
            Assert.Equal(
                [[1L, 1L]],
                await RowsAsync(
                    connectionString,
                    $"select (select count(*) from inventory.stock_reconciliations), (select count(*) from inventory.pouryakit_receipts where subscription = '{ReconcileStock}')"
                )
            );
            Assert.Empty(await RowsAsync(connectionString, "select id from wolverine.wolverine_dead_letters"));
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    // While an occurrence runs, nothing waits in the store, so a Host that starts then stores an occurrence of its own:
    // two chains, whose first common occurrence is applied once, and the one turned away there ends.
    [Fact]
    public async Task A_second_chain_a_starting_host_stores_while_an_occurrence_runs_applies_nothing_and_ends()
    {
        var connectionString = await NewDatabaseAsync();
        await using var running = Compose(connectionString, TimeSpan.FromSeconds(1));
        await using var starting = Compose(connectionString, TimeSpan.FromSeconds(1));
        var turnedAway = DeliveryProbes.TurnedAwayFrom(ReconcileStock);
        try
        {
            using (var held = DeliveryProbes.HoldAfterReceipt(ReconcileStock))
            {
                await HostFixture.StartHostAsync(running, TestContext.Current.CancellationToken);
                await held.Held;
                await HostFixture.StartHostAsync(starting, TestContext.Current.CancellationToken);
                Assert.Single(await RowsAsync(connectionString, $"select id from wolverine.wolverine_incoming_envelopes where {OnQueue} and status = 'Scheduled'"));
            }

            await WaitUntilAsync(connectionString, "(select count(*) from inventory.stock_reconciliations) >= 4");
        }
        finally
        {
            await starting.StopAsync(TestContext.Current.CancellationToken);
            await running.StopAsync(TestContext.Current.CancellationToken);
        }

        Assert.True(DeliveryProbes.TurnedAwayFrom(ReconcileStock) > turnedAway);
        Assert.Single(await RowsAsync(connectionString, Waiting));
        var reconciliations = await RowsAsync(connectionString, "select count(*), count(distinct occurrence) from inventory.stock_reconciliations");
        Assert.Equal(reconciliations[0][0], reconciliations[0][1]);
        Assert.Empty(await RowsAsync(connectionString, "select id from wolverine.wolverine_dead_letters"));
    }

    [Fact]
    public async Task A_host_whose_recurring_command_has_no_cadence_fails_to_start()
    {
        await using var host = Compose(Fixture.ConnectionString, TimeSpan.Zero);

        var thrown = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            $"The recurring command {ReconcileStock} has no cadence: set PouryaKitRecurringCommandOptions.Every, named {ReconcileStock}, longer than zero, as its declaration's options builder binds it.",
            thrown.Message
        );
    }

    private const string OnQueue = $"received_at = 'local://{ReconcileStock}/'";

    // Every stored occurrence not yet handled: waiting to come due, to be retried, or running.
    private const string Waiting = $"select status from wolverine.wolverine_incoming_envelopes where {OnQueue} and status <> 'Handled'";

    private WebApplication Compose(string connectionString, TimeSpan every) =>
        AppFixture.ComposeHost(
            "Development",
            connectionString,
            Fixture.Logs,
            builder => builder.Configuration["Inventory:ReconcileStock:Every"] = every.ToString()
        );

    private async Task<string> NewDatabaseAsync()
    {
        var database = $"recurring_{Guid.CreateVersion7():N}";
        await Fixture.RowsAsync($"create database {database}");
        return new NpgsqlConnectionStringBuilder(Fixture.ConnectionString) { Database = database }.ConnectionString;
    }

    private static async Task<List<object[]>> RowsAsync(string connectionString, string sql)
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

    private static async Task WaitUntilAsync(string connectionString, string condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!(bool)(await RowsAsync(connectionString, $"select {condition}"))[0][0])
        {
            Assert.False(timeout.IsCancellationRequested, $"Not reached within 15 s: {condition}");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }
}
