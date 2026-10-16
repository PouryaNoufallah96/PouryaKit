using M2.Ledger;

namespace M2.Tests;

// Item 12: the Marten Unit of Work is the scope's IReadingStateGuard, so the dispatcher compares, across a step that only
// reads, what a commit would persist: Streams started, events raised on the Streams it tracks, and operations queued on
// Marten's session (PendingChanges). A query sent from a command that raises an event is refused, and the command that
// swallows the refusal still commits nothing.
public sealed class ReadingStateTests(Postgres postgres) : SpikeTest(postgres)
{
    [Fact]
    public async Task A_query_that_changes_a_stream_marks_its_operation_rollback_only_and_nothing_commits()
    {
        var accountId = Guid.CreateVersion7();
        var host = await StartAsync();
        await Ledger(host).SendAsync(new OpenAccountCommand(accountId, Guid.CreateVersion7()), Context(), Token);
        await WaitUntilAsync("select count(*) = 1 from events.mt_events where stream_id = $1 and type = 'welcomed'", accountId);

        var result = await Ledger(host).SendAsync(new DepositThroughQueryCommand(accountId), Context(), Token);

        Assert.True(result.IsFailure);
        Assert.Equal(0, await CountAsync("select count(*) from events.mt_events where type = 'deposited'"));
    }

    // A Stream's Id is settable, for Marten's fold: a query that points a tracked Stream at another stream of the same
    // version while its events are pending is refused, and the append would go to the identity captured when tracking
    // began (review finding F4 of #120).
    [Fact]
    public async Task A_query_that_points_a_tracked_stream_at_another_while_its_events_are_pending_is_refused_and_nothing_commits()
    {
        var accountId = Guid.CreateVersion7();
        var otherId = Guid.CreateVersion7();
        var host = await StartAsync();
        await Ledger(host).SendAsync(new OpenAccountCommand(accountId, Guid.CreateVersion7()), Context(), Token);
        await Ledger(host).SendAsync(new OpenAccountCommand(otherId, Guid.CreateVersion7()), Context(), Token);
        await WaitUntilAsync("select count(*) = 2 from events.mt_events where type = 'welcomed'");

        var result = await Ledger(host).SendAsync(new DepositThenRedirectCommand(accountId, otherId), Context(), Token);

        Assert.True(result.IsFailure);
        Assert.Equal(0, await CountAsync("select count(*) from events.mt_events where type = 'deposited'"));
    }
}
