using M2.Host;
using M2.Ledger;
using PouryaKit.Persistence.EFCore;

namespace M2.Tests;

// Items 2, 3, 4 and 7: what the Marten Unit of Work commits through Wolverine's Marten outbox, read from the store between
// Marten's commit and Wolverine's flush, where a store listener runs.
public sealed class CommitTests(Postgres postgres) : SpikeTest(postgres)
{
    private const string Welcomed = "select count(*) from events.mt_events where stream_id = $1 and type = 'welcomed'";

    [Fact]
    public async Task An_envelope_commits_in_the_transaction_of_its_events_and_is_delivered_through_the_kits_routed_queue()
    {
        var accountId = Guid.CreateVersion7();
        var probe = new CommitProbe();
        List<object?[]>? atCommit = null;
        probe.OnCommit = async () => atCommit ??= await Db.RowsAsync(
            ConnectionString,
            """
            select pg_xact_commit_timestamp(e.xmin), pg_xact_commit_timestamp(w.xmin), w.status, w.owner_id, w.received_at
            from events.mt_events e, wolverine.wolverine_incoming_envelopes w where e.stream_id = $1
            """,
            accountId
        );
        var host = await StartAsync(new SpikeHostOptions { Marten = marten => marten.Listeners.Add(probe) });

        var opened = await Ledger(host).SendAsync(new OpenAccountCommand(accountId, Guid.CreateVersion7()), Context(), Token);

        Assert.True(opened.IsSuccess);
        var row = Assert.Single(atCommit!);
        Assert.NotNull(row[0]);
        Assert.Equal(row[0], row[1]);
        Assert.Equal("Incoming", row[2]);
        Assert.NotEqual(0, row[3]);
        Assert.Equal("local://ledger.welcome/", row[4]);
        await WaitUntilAsync($"select ({Welcomed}) = 1", accountId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stream_that_moved_after_it_was_read_is_the_kits_conflict_and_sends_nothing(bool whileTheFirstSaves)
    {
        var accountId = Guid.CreateVersion7();
        var probe = new CommitProbe();
        var host = await StartAsync(new SpikeHostOptions { Marten = marten => marten.Listeners.Add(probe) });
        await Ledger(host).SendAsync(new OpenAccountCommand(accountId, Guid.CreateVersion7()), Context(), Token);
        await WaitUntilAsync($"select ({Welcomed}) = 1", accountId);

        // The first deposit holds after its handler read the stream, or at its save, past FetchForWriting; the second
        // deposit commits meanwhile.
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (whileTheFirstSaves)
        {
            var held = 0;
            probe.OnBeforeSave = async () =>
            {
                if (Interlocked.Exchange(ref held, 1) == 0)
                {
                    reached.SetResult();
                    await release.Task;
                }
            };
        }
        else
        {
            var gate = LedgerProbe.Close(accountId);
            _ = gate.Reached.Task.ContinueWith(_ => reached.SetResult(), TaskScheduler.Default);
            _ = release.Task.ContinueWith(_ => gate.Opened.SetResult(), TaskScheduler.Default);
        }

        var first = Ledger(host).SendAsync(new DepositCommand(accountId, 10), Context(), Token);
        await reached.Task;
        var second = await Ledger(host).SendAsync(new DepositCommand(accountId, 20), Context(), Token);
        release.SetResult();
        var refused = await first;

        Assert.True(second.IsSuccess);
        Assert.Equal(UnitOfWorkErrors.ConcurrencyConflict, Assert.Single(refused.Errors));
        Assert.Equal(1, await CountAsync("select count(*) from events.mt_events where stream_id = $1 and type = 'deposited'", accountId));
        Assert.Equal(1, await CountAsync("select count(*) from wolverine.wolverine_incoming_envelopes where received_at = 'local://ledger.note-deposit/'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stream_started_twice_appends_nothing_and_sends_nothing_the_second_time(bool whileTheFirstSaves)
    {
        var accountId = Guid.CreateVersion7();
        var probe = new CommitProbe();
        var host = await StartAsync(new SpikeHostOptions { Marten = marten => marten.Listeners.Add(probe) });
        var command = new OpenAccountCommand(accountId, Guid.CreateVersion7());

        PouryaKit.Domain.Result first, second;
        if (whileTheFirstSaves)
        {
            // The first holds at its save, past FetchForWriting, while the second starts the same stream and commits.
            var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var held = 0;
            probe.OnBeforeSave = async () =>
            {
                if (Interlocked.Exchange(ref held, 1) == 0)
                {
                    saving.SetResult();
                    await release.Task;
                }
            };
            var running = Ledger(host).SendAsync(command, Context(), Token);
            await saving.Task;
            second = await Ledger(host).SendAsync(command, Context(), Token);
            release.SetResult();
            first = await running;
            (first, second) = (second, first);
        }
        else
        {
            first = await Ledger(host).SendAsync(command, Context(), Token);
            second = await Ledger(host).SendAsync(command, Context(), Token);
        }

        Assert.True(first.IsSuccess);
        Assert.Equal(UnitOfWorkErrors.ConcurrencyConflict, Assert.Single(second.Errors));
        Assert.Equal(1, await CountAsync("select count(*) from events.mt_events where stream_id = $1 and type = 'account_opened'", accountId));
        Assert.Equal(1, await CountAsync("select count(*) from wolverine.wolverine_incoming_envelopes where received_at = 'local://ledger.welcome/'"));
    }

    [Fact]
    public async Task A_notification_handlers_integration_event_and_receipt_commit_with_its_stream_effect()
    {
        var accountId = Guid.CreateVersion7();
        var probe = new CommitProbe();
        List<object?[]>? atCommit = null;
        probe.OnCommit = async () =>
        {
            if (atCommit is null && await CountAsync($"({Welcomed})", accountId) == 1)
            {
                atCommit = await Db.RowsAsync(
                    ConnectionString,
                    """
                    select pg_xact_commit_timestamp(xmin) from events.mt_events where stream_id = $1 and type = 'welcomed'
                    union all select pg_xact_commit_timestamp(xmin) from ledger.pouryakit_receipts where subscription = 'ledger.welcome'
                    union all select pg_xact_commit_timestamp(xmin) from ledger.pouryakit_receipts where subscription = 'ledger.account-opened.v1#published'
                    union all select pg_xact_commit_timestamp(xmin) from wolverine.wolverine_incoming_envelopes where received_at = 'local://catalog.account-opened/'
                    """,
                    accountId
                );
            }
        };
        var host = await StartAsync(new SpikeHostOptions { Marten = marten => marten.Listeners.Add(probe) });

        await Ledger(host).SendAsync(new OpenAccountCommand(accountId, Guid.CreateVersion7()), Context(), Token);
        await WaitUntilAsync("select count(*) = 1 from catalog.pouryakit_receipts where subscription = 'catalog.account-opened'");

        Assert.Equal(4, atCommit!.Count);
        Assert.NotNull(atCommit[0][0]);
        Assert.All(atCommit, row => Assert.Equal(atCommit[0][0], row[0]));
    }
}
