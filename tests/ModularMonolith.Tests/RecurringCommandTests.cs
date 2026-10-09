using System.Buffers.Binary;
using System.Globalization;
using Inventory;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModularMonolith.Tests.Stub;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;
using PouryaKit.Testing;
using Wolverine;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Runtime;

namespace ModularMonolith.Tests;

/// <summary>
/// Inventory's ReconcileStock is recurring work: each occurrence of its cadence runs as an Operation of its own for the
/// system Actor its Composition Root names, and stores the next occurrence in its commit, so whichever Host runs next
/// goes on with it. Each test runs its Hosts on a database of their own, so the fixture's Host never runs an occurrence;
/// the points where a test stops a Host or hands an occurrence out again are held by probes, not left to the cadence;
/// and the tests observe what the occurrences committed, never how often the transport ran a handler.
/// </summary>
public sealed class RecurringCommandTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private const string ReconcileStock = "inventory.reconcile-stock";
    private const string Reconciler = "inventory.stock-reconciler";
    private const string KindChanged = "stub.kind-changed";

    [Fact]
    public async Task ReconcileStock_runs_under_its_system_actor_and_the_occurrence_a_dying_host_stored_runs_after_a_restart()
    {
        var connectionString = await NewDatabaseAsync();
        await using var dying = Compose(connectionString, TimeSpan.FromSeconds(3));
        await using var starting = Compose(connectionString, TimeSpan.FromSeconds(3));
        await HostFixture.StartHostAsync(dying, TestContext.Current.CancellationToken);
        await WaitUntilAsync(connectionString, "exists (select from inventory.stock_reconciliations)");
        object[] stored;
        DateTime died;
        try
        {
            using (var committed = DeliveryProbes.HoldAfterCommit(typeof(InventoryModule), ReconcileStock))
            {
                // An occurrence committed its reconciliation and the next occurrence, and is not yet acknowledged.
                await committed.Held;
                stored = Assert.Single(
                    await RowsAsync(connectionString, $"select id, execution_time from wolverine.wolverine_incoming_envelopes where {OnQueue} and status = 'Scheduled'")
                );
                // Wolverine's quick stop neither drains nor gives up what the Host holds, as a killed process leaves it.
                ((WolverineRuntime)dying.Services.GetRequiredService<IWolverineRuntime>()).StopMode = StopMode.Quick;
                await dying.StopAsync(TestContext.Current.CancellationToken);
                died = (DateTime)(await RowsAsync(connectionString, "select now()"))[0][0];

                await HostFixture.StartHostAsync(starting, TestContext.Current.CancellationToken);
                await WaitUntilAsync(connectionString, $"exists (select from wolverine.wolverine_incoming_envelopes where id = '{stored[0]}' and status = 'Handled')");
            }
        }
        finally
        {
            await starting.StopAsync(TestContext.Current.CancellationToken);
        }

        // The occurrence stored before the Host died ran after the restart, under the system Actor, with its Receipt.
        var occurrence = (DateTime)stored[1];
        Assert.Equal(
            [[Reconciler, true]],
            await RowsAsync(
                connectionString,
                $"select reconciled_by, pg_xact_commit_timestamp(xmin) > '{died:O}' from inventory.stock_reconciliations where occurrence = '{occurrence:O}'"
            )
        );
        Assert.Equal(
            [[1L]],
            await RowsAsync(
                connectionString,
                $"select count(*) from inventory.pouryakit_receipts where subscription = '{ReconcileStock}' and message_id = '{ReceiptOf(occurrence)}'"
            )
        );
        // At least one more ran before it, each occurrence once, on whole seconds, by the system Actor, with its Receipt.
        Assert.Equal(
            [[true, true, true, true]],
            await RowsAsync(
                connectionString,
                $"""
                select count(*) >= 2, count(*) = count(distinct occurrence),
                       bool_and(date_trunc('second', occurrence) = occurrence and reconciled_by = '{Reconciler}'),
                       count(*) = (select count(*) from inventory.pouryakit_receipts where subscription = '{ReconcileStock}')
                from inventory.stock_reconciliations
                """
            )
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

    // An hourly cadence, so nothing comes due: only what each start stores is seen.
    [Fact]
    public async Task A_starting_host_stores_the_first_occurrence_only_when_none_waits()
    {
        var connectionString = await NewDatabaseAsync();
        await using var first = Compose(connectionString, TimeSpan.FromHours(1));
        await using var second = Compose(connectionString, TimeSpan.FromHours(1));

        await HostFixture.StartHostAsync(first, TestContext.Current.CancellationToken);
        var waiting = await RowsAsync(
            connectionString,
            $"select id, execution_time > now() and execution_time <= now() + interval '1 hour' and date_trunc('hour', execution_time) = execution_time from wolverine.wolverine_incoming_envelopes where {OnQueue}"
        );
        await first.StopAsync(TestContext.Current.CancellationToken);
        await HostFixture.StartHostAsync(second, TestContext.Current.CancellationToken);
        List<object[]> afterSecond;
        try
        {
            afterSecond = await RowsAsync(connectionString, $"select id from wolverine.wolverine_incoming_envelopes where {OnQueue}");
        }
        finally
        {
            await second.StopAsync(TestContext.Current.CancellationToken);
        }

        // The first start stored the next whole hour; the second found it waiting and stored nothing.
        Assert.True((bool)Assert.Single(waiting)[1]);
        Assert.Equal([[waiting[0][0]]], afterSecond);
    }

    [Fact]
    public async Task An_occurrence_delivered_again_finds_its_receipt_and_neither_reconciles_again_nor_stores_another_occurrence()
    {
        var connectionString = await NewDatabaseAsync();
        await using var host = Compose(connectionString, TimeSpan.FromSeconds(2));
        DeliveryProbes.Hold? later = null;
        object[] occurrence;
        object[] next;
        try
        {
            using (var committed = DeliveryProbes.HoldAfterCommit(typeof(InventoryModule), ReconcileStock))
            {
                await HostFixture.StartHostAsync(host, TestContext.Current.CancellationToken);
                // The first occurrence committed, and is not yet acknowledged.
                await committed.Held;
                occurrence = Assert.Single(
                    await RowsAsync(connectionString, $"select id from wolverine.wolverine_incoming_envelopes where {OnQueue} and status = 'Incoming'")
                );
                next = Assert.Single(
                    await RowsAsync(
                        connectionString,
                        $"select e.id, pg_xact_commit_timestamp(e.xmin) = pg_xact_commit_timestamp(r.xmin) from wolverine.wolverine_incoming_envelopes e, inventory.stock_reconciliations r where e.{OnQueue} and e.status = 'Scheduled'"
                    )
                );
                // The next occurrence, when due, waits after its Receipt until the test ends, so it commits nothing here.
                later = DeliveryProbes.HoldAfterReceipt(ReconcileStock);
            }

            await WaitUntilAsync(connectionString, Handled(occurrence[0]));
            var turnedAway = DeliveryProbes.TurnedAwayFrom(ReconcileStock);

            // Pending again, as after a Host that died before acknowledging it, or a replay.
            Assert.Single(
                await RowsAsync(
                    connectionString,
                    $"update wolverine.wolverine_incoming_envelopes set status = 'Incoming', owner_id = 0 where id = '{occurrence[0]}' returning id"
                )
            );
            await WaitUntilAsync(connectionString, Handled(occurrence[0]));

            Assert.Equal(turnedAway + 1, DeliveryProbes.TurnedAwayFrom(ReconcileStock));
            Assert.Equal(
                [[1L, 1L]],
                await RowsAsync(
                    connectionString,
                    $"select (select count(*) from inventory.stock_reconciliations), (select count(*) from inventory.pouryakit_receipts where subscription = '{ReconcileStock}')"
                )
            );
            // Nothing on the queue besides the two occurrences: the redelivery stored no occurrence of its own.
            Assert.Empty(
                await RowsAsync(
                    connectionString,
                    $"select id from wolverine.wolverine_incoming_envelopes where {OnQueue} and id not in ('{occurrence[0]}', '{next[0]}')"
                )
            );
            Assert.Empty(await RowsAsync(connectionString, "select id from wolverine.wolverine_dead_letters"));
        }
        finally
        {
            later?.Dispose();
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        // The next occurrence was stored in the commit of the first one's reconciliation.
        Assert.True((bool)next[1]);
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

            // Two chains would keep two occurrences stored for good; one remains once they met.
            await WaitUntilAsync(
                connectionString,
                $"(select count(*) from inventory.stock_reconciliations) >= 4 and (select count(*) from wolverine.wolverine_incoming_envelopes where {OnQueue} and status <> 'Handled') = 1"
            );
        }
        finally
        {
            await starting.StopAsync(TestContext.Current.CancellationToken);
            await running.StopAsync(TestContext.Current.CancellationToken);
        }

        Assert.True(DeliveryProbes.TurnedAwayFrom(ReconcileStock) > turnedAway);
        var reconciliations = await RowsAsync(connectionString, "select count(*), count(distinct occurrence) from inventory.stock_reconciliations");
        Assert.Equal(reconciliations[0][0], reconciliations[0][1]);
        Assert.Empty(await RowsAsync(connectionString, "select id from wolverine.wolverine_dead_letters"));
    }

    // Every attempt fails, as while its database is down for longer than the stored retries, until the first occurrence is
    // set aside; no Host starts again and nothing is replayed, so only an attempt that set one aside stored what runs next.
    [Fact]
    public async Task An_occurrence_set_aside_as_a_dead_letter_stores_the_next_occurrence_which_runs()
    {
        var connectionString = await NewDatabaseAsync();
        await using var host = Compose(connectionString, TimeSpan.FromSeconds(1));
        List<DateTimeOffset> setAside;
        try
        {
            using (DeliveryProbes.FailAfterReceipt(ReconcileStock))
            {
                await HostFixture.StartHostAsync(host, TestContext.Current.CancellationToken);
                await WaitUntilAsync(connectionString, $"exists (select from wolverine.wolverine_dead_letters where {OnQueue})");
            }

            await WaitUntilAsync(connectionString, "exists (select from inventory.stock_reconciliations)");
            // An occurrence stored close to the first dead letter may have come due, and been set aside, before the failure ended.
            var deadLetters = await host
                .Services.GetRequiredService<IWolverineRuntime>()
                .Storage.DeadLetters.QueryAsync(new DeadLetterEnvelopeQuery { PageSize = 0 }, TestContext.Current.CancellationToken);
            setAside =
            [
                .. deadLetters.Envelopes.Select(deadLetter =>
                    DateTimeOffset.Parse(deadLetter.Envelope.Headers[DelayedCommandHeaders.Occurrence]!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                ),
            ];
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        // Each occurrence set aside was attempted once more than there are retry delays, and applied nothing.
        Assert.NotEmpty(setAside);
        Assert.All(
            setAside,
            occurrence => Assert.Equal(
                1 + TestDeliveryServiceCollectionExtensions.RetryDelays.Count,
                DeliveryProbes.AttemptsOf(ReconcileStock, ReceiptOf(occurrence.UtcDateTime)).Count
            )
        );
        // What ran came after every occurrence set aside, under the system Actor, each with its Receipt.
        Assert.Equal(
            [[true, true]],
            await RowsAsync(
                connectionString,
                $"""
                select bool_and(occurrence > '{setAside.Max():O}' and reconciled_by = '{Reconciler}'),
                       count(*) = (select count(*) from inventory.pouryakit_receipts where subscription = '{ReconcileStock}')
                from inventory.stock_reconciliations
                """
            )
        );
    }

    // Every attempt but the last fails: none of them sets the occurrence aside, so none stores an occurrence of its own.
    [Fact]
    public async Task An_occurrence_that_succeeds_on_its_last_attempt_leaves_only_the_next_occurrence_its_commit_stored()
    {
        var connectionString = await NewDatabaseAsync();
        await using var host = Compose(connectionString, TimeSpan.FromSeconds(1));
        List<object[]> reconciled;
        List<object[]> waiting;
        try
        {
            using (DeliveryProbes.FailAfterReceipt(ReconcileStock, TestDeliveryServiceCollectionExtensions.RetryDelays.Count))
            using (var committed = DeliveryProbes.HoldAfterCommit(typeof(InventoryModule), ReconcileStock))
            {
                await HostFixture.StartHostAsync(host, TestContext.Current.CancellationToken);
                // The last attempt committed, and is not yet acknowledged.
                await committed.Held;
                reconciled = await RowsAsync(connectionString, "select occurrence from inventory.stock_reconciliations");
                waiting = await RowsAsync(
                    connectionString,
                    $"select pg_xact_commit_timestamp(e.xmin) = pg_xact_commit_timestamp(r.xmin) from wolverine.wolverine_incoming_envelopes e, inventory.stock_reconciliations r where e.{OnQueue} and e.status = 'Scheduled'"
                );
            }
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            1 + TestDeliveryServiceCollectionExtensions.RetryDelays.Count,
            DeliveryProbes.AttemptsOf(ReconcileStock, ReceiptOf((DateTime)Assert.Single(reconciled)[0])).Count
        );
        // One occurrence waits, the one stored in the reconciliation's commit.
        Assert.True((bool)Assert.Single(waiting)[0]);
    }

    // A release that turned a delayed command's declaration into a recurring one, under the same name, while one of its
    // delayed commands waited in the store.
    [Fact]
    public async Task A_delayed_command_stored_before_its_name_was_declared_recurring_is_dead_lettered_and_never_run()
    {
        var connectionString = await NewDatabaseAsync();
        var text = $"kind {Guid.CreateVersion7()}";
        await using var delayed = Compose(
            connectionString,
            TimeSpan.FromHours(1),
            builder => builder.Services.AddPouryaKitDelayedCommand<WriteKindChangedNoteCommand>(KindChanged, typeof(StubModule))
        );
        await using var recurring = Compose(
            connectionString,
            TimeSpan.FromHours(1),
            builder =>
                builder
                    .Services.AddPouryaKitRecurringCommand(
                        KindChanged,
                        systemActor: "stub.kind-changer",
                        occurrence => new WriteKindChangedNoteCommand($"recurring {occurrence:O}"),
                        typeof(StubModule)
                    )
                    .Configure(options => options.Every = TimeSpan.FromHours(1))
        );
        await HostFixture.StartHostAsync(delayed, TestContext.Current.CancellationToken);
        Assert.True(
            (
                await delayed.Services.GetRequiredService<IGatewayProcessor<StubModule>>()
                    .SendAsync(new ScheduleKindChangedNoteCommand(text), TestContext.Current.CancellationToken)
            ).IsSuccess
        );
        await delayed.StopAsync(TestContext.Current.CancellationToken);

        await HostFixture.StartHostAsync(recurring, TestContext.Current.CancellationToken);
        try
        {
            await WaitUntilAsync(connectionString, $"exists (select from wolverine.wolverine_dead_letters where received_at = 'local://{KindChanged}/')");
        }
        finally
        {
            await recurring.StopAsync(TestContext.Current.CancellationToken);
        }

        // Refused before it began any Operation: no Receipt, no note, no attempt under the system Actor.
        Assert.Equal(
            [[typeof(UnknownDelayedCommandException).FullName]],
            await RowsAsync(connectionString, $"select exception_type from wolverine.wolverine_dead_letters where received_at = 'local://{KindChanged}/'")
        );
        Assert.Equal(
            [[0L, 0L]],
            await RowsAsync(
                connectionString,
                $"select (select count(*) from stub.notes where text = '{text}'), (select count(*) from stub.pouryakit_receipts where subscription = '{KindChanged}')"
            )
        );
        Assert.Empty(DeliveryProbes.ContextsOf(KindChanged));
    }

    [Fact]
    public void A_recurring_command_declared_again_with_another_occurrence_factory_is_refused_naming_the_declaration_it_has()
    {
        var services = new ServiceCollection();
        services.AddPouryaKitRecurringCommand(KindChanged, "stub.kind-changer", _ => new WriteKindChangedNoteCommand("first"), typeof(StubModule));

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            services.AddPouryaKitRecurringCommand(KindChanged, "stub.kind-changer", _ => new WriteKindChangedNoteCommand("second"), typeof(StubModule))
        );

        Assert.Equal(
            $"{typeof(WriteKindChangedNoteCommand).FullName} is already declared as the recurring command {KindChanged} of StubModule; a command is declared once, as a delayed or a recurring command, and a recurring one with one system Actor and one occurrence factory.",
            thrown.Message
        );
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

    private static string Handled(object id) =>
        $"exists (select from wolverine.wolverine_incoming_envelopes where id = '{id}' and status = 'Handled')";

    // The Receipt key the Kit records for an occurrence: its time, in UTC ticks, in the key's first eight bytes.
    private static Guid ReceiptOf(DateTime occurrence)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt64BigEndian(bytes, new DateTimeOffset(occurrence, TimeSpan.Zero).UtcTicks);
        return new Guid(bytes, bigEndian: true);
    }

    private WebApplication Compose(string connectionString, TimeSpan every, Action<WebApplicationBuilder>? configure = null) =>
        AppFixture.ComposeHost(
            "Development",
            connectionString,
            Fixture.Logs,
            builder =>
            {
                builder.Configuration["Inventory:ReconcileStock:Every"] = every.ToString();
                configure?.Invoke(builder);
            }
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
