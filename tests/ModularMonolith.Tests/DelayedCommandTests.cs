using Billing;
using Billing.Quotes.Features.OpenQuote;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
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
        await Fixture.WaitForDeliveriesAsync(
            () => billing.SendAsync(new OpenQuoteCommand(quote, TimeSpan.FromMilliseconds(300)), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken
        );
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
