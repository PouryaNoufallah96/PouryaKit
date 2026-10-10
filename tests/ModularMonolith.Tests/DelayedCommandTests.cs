using System.Diagnostics;
using Billing;
using Billing.Quotes.Features.ExpireQuote;
using Billing.Quotes.Features.OpenQuote;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Modularity;
using PouryaKit.Persistence.Postgres;
using PouryaKit.Testing;
using Wolverine;
using Wolverine.Runtime;

namespace ModularMonolith.Tests;

/// <summary>
/// A command an Operation schedules for later is stored with that Operation's commit, waits in the message store until it
/// is due, whichever Host is running then, and runs once, as a continuation of the Operation that scheduled it.
/// </summary>
public sealed class DelayedCommandTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private const string ExpireQuote = "billing.expire-quote";

    // The Host that scheduled the expiry dies before it is due, and only a Host started afterwards runs on the database:
    // a database of its own, so the fixture's Host, alive throughout, cannot run it instead.
    [Fact]
    public async Task An_expiry_scheduled_by_a_host_that_dies_before_it_is_due_runs_once_after_a_host_starts_with_the_scheduling_context()
    {
        var connectionString = await NewDatabaseAsync();
        var quote = Guid.CreateVersion7();
        var opening = new OperationContext(Actor.Person("alice"), "tenant-a", Guid.CreateVersion7());
        await using var dying = AppFixture.ComposeHost("Development", connectionString, Fixture.Logs);
        await using var starting = AppFixture.ComposeHost("Development", connectionString, Fixture.Logs);
        await HostFixture.StartHostAsync(dying, TestContext.Current.CancellationToken);

        var opened = await dying.Services.GetRequiredService<IGatewayProcessor<BillingModule>>()
            .SendAsync(new OpenQuoteCommand(quote, TimeSpan.FromSeconds(3)), opening, TestContext.Current.CancellationToken);
        // Wolverine's quick stop neither drains nor gives up what the Host holds, as a killed process leaves it.
        ((WolverineRuntime)dying.Services.GetRequiredService<IWolverineRuntime>()).StopMode = StopMode.Quick;
        await dying.StopAsync(TestContext.Current.CancellationToken);

        // Stored with the quote, in its commit, due at its expiry, and owned by no Host.
        var stored = Assert.Single(
            await RowsAsync(
                connectionString,
                $"""
                select e.id, e.status, e.owner_id, e.execution_time = q.expires_on, q.state,
                       pg_xact_commit_timestamp(e.xmin) = pg_xact_commit_timestamp(q.xmin), q.expires_on > now()
                from wolverine.wolverine_incoming_envelopes e, billing.quotes q
                where e.received_at = 'local://{ExpireQuote}/' and q.id = '{quote}'
                """
            )
        );
        var envelope = (Guid)stored[0];
        Assert.True(opened.IsSuccess);
        Assert.Equal(["Scheduled", 0, true, "Open", true, true], stored[1..]);

        try
        {
            await HostFixture.StartHostAsync(starting, TestContext.Current.CancellationToken);
            await WaitUntilAsync(
                connectionString,
                $"exists (select from wolverine.wolverine_incoming_envelopes where id = '{envelope}' and status = 'Handled')"
            );
        }
        finally
        {
            await starting.StopAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            [["Expired", 2L, "alice"]],
            await RowsAsync(connectionString, $"select state, version, last_changed_by from billing.quotes where id = '{quote}'")
        );
        Assert.Equal(
            [[ExpireQuote, envelope]],
            await RowsAsync(connectionString, "select subscription, message_id from billing.pouryakit_receipts")
        );
        Assert.Empty(await RowsAsync(connectionString, "select id from wolverine.wolverine_dead_letters"));
        // Every attempt continued the Operation that opened the quote: its Actor, tenant and correlation, caused by it.
        var contexts = DeliveryProbes.ContextsOf(ExpireQuote, envelope);
        Assert.NotEmpty(contexts);
        Assert.All(
            contexts,
            context => Assert.Equal(
                (opening.Actor, opening.Tenant, opening.CorrelationId, (Guid?)opening.OperationId),
                (context.Actor, context.Tenant, context.CorrelationId, context.CausationId)
            )
        );
    }

    [Fact]
    public async Task A_delayed_command_handed_out_again_after_it_ran_finds_its_receipt_and_applies_nothing_more()
    {
        var quote = Guid.CreateVersion7();
        var billing = Fixture.Gateway<BillingModule>();
        await billing.SendAsync(new OpenQuoteCommand(quote, TimeSpan.FromMilliseconds(300)), TestContext.Current.CancellationToken);
        await Fixture.WaitUntilAsync(Handled(ExpireQuote), "the expiry ran");
        var expired = await Fixture.RowsAsync($"select state, version, last_changed_at from billing.quotes where id = '{quote}'");
        var turnedAway = DeliveryProbes.TurnedAwayFrom(ExpireQuote);

        // Pending again, as after a Host that died before acknowledging it, or a replay.
        Assert.Single(
            await Fixture.RowsAsync(
                $"update wolverine.wolverine_incoming_envelopes set status = 'Incoming', owner_id = 0 where received_at = 'local://{ExpireQuote}/' returning id"
            )
        );
        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);

        // Run again, the expiry of an expired quote would fail, and be retried and set aside.
        Assert.Equal("Expired", expired[0][0]);
        Assert.Empty(await Fixture.DeadLettersAsync(TestContext.Current.CancellationToken));
        Assert.Equal(expired, await Fixture.RowsAsync($"select state, version, last_changed_at from billing.quotes where id = '{quote}'"));
        Assert.Equal(turnedAway + 1, DeliveryProbes.TurnedAwayFrom(ExpireQuote));
        Assert.Equal(1L, await Fixture.ScalarAsync<long>($"select count(*) from billing.pouryakit_receipts where subscription = '{ExpireQuote}'"));
    }

    [Theory]
    [InlineData(SchedulingFailure.Result)]
    [InlineData(SchedulingFailure.Nested)]
    public async Task A_scheduling_operation_that_fails_or_whose_nested_command_fails_leaves_no_delayed_command(SchedulingFailure failure)
    {
        var text = $"note {Guid.CreateVersion7()}";

        var result = await Fixture.AssertNothingSentAsync(
            () => Fixture.Gateway<StubModule>().SendAsync(new ScheduleNoteThenFailCommand(text, failure), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsFailure);
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from wolverine.wolverine_incoming_envelopes"));
        Assert.Equal(0L, await Fixture.ScalarAsync<long>($"select count(*) from stub.notes where text like '%{text}'"));
    }

    [Fact]
    public async Task Scheduling_a_command_the_module_did_not_declare_throws_and_commits_nothing()
    {
        var text = $"note {Guid.CreateVersion7()}";

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Fixture.AssertNothingSentAsync(
                () =>
                    Fixture
                        .Gateway<StubModule>()
                        .SendAsync(new ScheduleNoteThenFailCommand(text, SchedulingFailure.Undeclared), TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken
            )
        );

        Assert.StartsWith($"StubModule does not declare that it schedules {typeof(WriteNoteCommand).FullName}.", thrown.Message);
        Assert.Equal(0L, await Fixture.ScalarAsync<long>($"select count(*) from stub.notes where text = '{text}'"));
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from wolverine.wolverine_incoming_envelopes"));
    }

    // Each of them runs inside a Unit of Work, so the refusal of a call outside one leaves them be: a Domain Event
    // Handler in the Operation that raised its Domain Event, a Notification Handler in its delivery, and a delayed
    // command, due at once here, in its own run.
    [Fact]
    public async Task A_domain_event_handler_a_notification_handler_and_a_delayed_commands_own_run_each_schedule()
    {
        var text = $"note {Guid.CreateVersion7()}";
        var ranFromTheNotificationHandler = ScheduleFollowUpNotificationHandler.TextOf(text);

        var written = await Fixture
            .Gateway<StubModule>()
            .SendAsync(new WriteNoteRaisingCommand(text, [new NoteToFollowUpDomainEvent(text)]), TestContext.Current.CancellationToken);
        await Fixture.WaitUntilAsync(
            $"exists (select from stub.notes where text = '{ranFromTheNotificationHandler}')",
            "the note the Notification Handler scheduled was written"
        );
        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);

        Assert.True(written.IsSuccess);
        // The Domain Event Handler's and the delayed run's wait their hour; the Notification Handler's has run.
        Assert.Equal(
            [["Handled", 1L], ["Scheduled", 2L]],
            await Fixture.RowsAsync(
                "select status, count(*) from wolverine.wolverine_incoming_envelopes where received_at = 'local://stub.delayed-note/' group by status order by status"
            )
        );
        Assert.Empty(await Fixture.DeadLettersAsync(TestContext.Current.CancellationToken));
    }

    // Every delayed command runs as the same Kit command, so the run's Operation names the declaration beside it.
    [Fact]
    public async Task A_delayed_commands_run_is_an_operation_tagged_with_its_name()
    {
        using var telemetry = new RecordedTelemetry();
        var earlier = DeliveryProbes.ActivitiesOf("stub.delayed-note").Count;
        var text = $"note {Guid.CreateVersion7()}";

        await Fixture
            .Gateway<StubModule>()
            .SendAsync(new WriteNoteRaisingCommand(text, [new NoteToFollowUpDomainEvent(text)]), TestContext.Current.CancellationToken);
        await Fixture.WaitUntilAsync(
            $"exists (select from stub.notes where text = '{ScheduleFollowUpNotificationHandler.TextOf(text)}')",
            "the note the Notification Handler scheduled was written"
        );
        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);

        var run = Assert.IsType<Activity>(Assert.Single(DeliveryProbes.ActivitiesOf("stub.delayed-note").Skip(earlier)));
        Assert.Equal("stub.delayed-note", run.GetTagItem("pouryakit.subscription"));
    }

    // Only a Unit of Work's commit writes what a scope scheduled, so outside one the command would be dropped with the
    // scope. A scope never begun names no Module either, and is told of the Unit of Work, not of a missing declaration.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Scheduling_in_a_scope_of_a_host_of_modules_that_runs_no_unit_of_work_throws_and_stores_nothing(bool begun)
    {
        Exception? thrown;
        await using (var scope = Fixture.Host.Services.CreateAsyncScope())
        {
            if (begun)
            {
                scope.ServiceProvider.BeginOperation(new OperationContext(Actor.Person("ada")), typeof(StubModule));
            }

            thrown = Record.Exception(() => ScheduleNote(scope.ServiceProvider));
        }

        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from wolverine.wolverine_incoming_envelopes"));
        Assert.StartsWith(NoUnitOfWork, Assert.IsType<InvalidOperationException>(thrown).Message);
    }

    // Built and never started: scheduling reaches no database. With no Module to name, nothing else refused the call.
    [Fact]
    public void Scheduling_in_a_scope_of_an_application_without_modules_that_runs_no_unit_of_work_throws()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            ["--environment", "Production", $"--ConnectionStrings:app={Fixture.ConnectionString}"]
        );
        builder.Services.UsePouryaKitPostgres<StubDbContext>(connectionName: "app", schema: "stub");
        builder.Services.AddPouryaKitDelayedCommand<WriteDelayedNoteCommand>("tests.delayed-note");
        builder.UsePouryaKitWolverine("app");
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.BeginOperation(new OperationContext(Actor.Person("ada")));

        var thrown = Assert.Throws<InvalidOperationException>(() => ScheduleNote(scope.ServiceProvider));

        Assert.StartsWith(NoUnitOfWork, thrown.Message);
    }

    // The quote's second opening is refused at the save, after the Outbox step wrote its expiry in the transaction.
    [Fact]
    public async Task A_scheduling_operation_whose_save_is_refused_after_the_outbox_was_written_leaves_no_delayed_command()
    {
        var quote = Guid.CreateVersion7();
        var billing = Fixture.Gateway<BillingModule>();
        Assert.True((await billing.SendAsync(new OpenQuoteCommand(quote, TimeSpan.FromHours(1)), TestContext.Current.CancellationToken)).IsSuccess);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            billing.SendAsync(new OpenQuoteCommand(quote, TimeSpan.FromHours(2)), TestContext.Current.CancellationToken)
        );

        // Only the first opening's expiry, due in an hour, is stored.
        Assert.Equal(
            [["Scheduled", true]],
            await Fixture.RowsAsync(
                $"select e.status, e.execution_time = q.expires_on from wolverine.wolverine_incoming_envelopes e, billing.quotes q where e.received_at = 'local://{ExpireQuote}/' and q.id = '{quote}'"
            )
        );
    }

    [Fact]
    public async Task A_delayed_command_not_yet_due_is_not_waited_for_and_the_reset_removes_it()
    {
        var quote = Guid.CreateVersion7();

        var (_, deliveries) = await Fixture.WaitForDeliveriesAsync(
            () => Fixture.Gateway<BillingModule>().SendAsync(new OpenQuoteCommand(quote, TimeSpan.FromHours(1)), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken
        );
        var stored = await Fixture.RowsAsync($"select status from wolverine.wolverine_incoming_envelopes where received_at = 'local://{ExpireQuote}/'");
        await Fixture.ResetAsync(TestContext.Current.CancellationToken);

        Assert.Equal([new Delivery(ExpireQuote, typeof(ExpireQuoteCommand).FullName!, false)], deliveries);
        Assert.Equal([["Scheduled"]], stored);
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from wolverine.wolverine_incoming_envelopes"));
    }

    // As after a release that renamed the declaration while the expiry waited: stored under a name nothing declares.
    [Fact]
    public async Task A_delayed_command_stored_under_a_name_no_longer_declared_is_dead_lettered_and_never_run()
    {
        var quote = Guid.CreateVersion7();
        await Fixture.Gateway<BillingModule>().SendAsync(new OpenQuoteCommand(quote, TimeSpan.FromSeconds(2)), TestContext.Current.CancellationToken);
        Assert.Single(
            await Fixture.RowsAsync(
                $"update wolverine.wolverine_incoming_envelopes set received_at = 'local://billing.expire-quote-retired/' where received_at = 'local://{ExpireQuote}/' and status = 'Scheduled' returning id"
            )
        );

        await Fixture.WaitUntilAsync("exists (select from wolverine.wolverine_dead_letters)", "the retired expiry was set aside");
        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);

        var deadLetter = Assert.Single(await Fixture.DeadLettersAsync(TestContext.Current.CancellationToken));
        Assert.Equal(("billing.expire-quote-retired", typeof(UnknownDelayedCommandException).FullName), (deadLetter.Subscription, deadLetter.ExceptionType));
        Assert.Equal("Open", await Fixture.ScalarAsync<string>($"select state from billing.quotes where id = '{quote}'"));
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from billing.pouryakit_receipts"));
    }

    // Ignoring case, as Wolverine lower-cases a local queue's name.
    [Theory]
    [InlineData("scheduled")]
    [InlineData("ScheDuled")]
    public void A_delayed_command_named_after_a_queue_wolverine_keeps_for_itself_is_refused(string name)
    {
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddPouryaKitDelayedCommand<WriteDelayedNoteCommand>(name)
        );

        Assert.Equal(
            $"{typeof(WriteDelayedNoteCommand).FullName}'s delayed command name {name} is a queue Wolverine keeps for itself; choose another.",
            thrown.Message
        );
    }

    private const string NoUnitOfWork =
        "WriteDelayedNoteCommand was scheduled while no Unit of Work is active, so no commit would write it to the Outbox and it would never be stored. Schedule a command from a command's handler, a Domain Event Handler or a Notification Handler.";

    // A command the scope's Host declares, scheduled as a handler would schedule it.
    private static void ScheduleNote(IServiceProvider scope) =>
        scope.GetRequiredService<IDelayedCommands>().Schedule(new WriteDelayedNoteCommand("never stored"), DateTimeOffset.UtcNow.AddHours(1));

    private static string Handled(string name) =>
        $"exists (select from wolverine.wolverine_incoming_envelopes where received_at = 'local://{name}/' and status = 'Handled')";

    private async Task<string> NewDatabaseAsync()
    {
        var database = $"delayed_{Guid.CreateVersion7():N}";
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
