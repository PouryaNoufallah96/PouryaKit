using M2.Ledger;

namespace M2.Tests;

// Marten's SaveChangesAsync returns without committing when the session queued no work, so a Receipt or an idempotency key
// that is an Operation's only write would be rolled back with the session: the Marten Unit of Work commits its own
// transaction then (review finding F1 of #120).
public sealed class CommitWithoutWorkTests(Postgres postgres) : SpikeTest(postgres)
{
    [Fact]
    public async Task A_notification_handler_that_writes_nothing_keeps_its_receipt_and_a_redelivery_never_runs_it()
    {
        var accountId = Guid.CreateVersion7();
        var host = await StartAsync();
        await Ledger(host).SendAsync(new OpenAccountCommand(accountId, Guid.CreateVersion7()), Context(), Token);
        await WaitUntilAsync("select count(*) = 1 from events.mt_events where stream_id = $1 and type = 'welcomed'", accountId);

        await Ledger(host).SendAsync(new DepositCommand(accountId, 10), Context(), Token);
        await WaitUntilAsync("select count(*) = 1 from wolverine.wolverine_incoming_envelopes where received_at = 'local://ledger.note-deposit/' and status = 'Handled'");
        var deposited = await Db.ScalarAsync<Guid>(ConnectionString, "select (data ->> 'id')::uuid from events.mt_events where stream_id = $1 and type = 'deposited'", accountId);

        Assert.Equal(1, await CountAsync("select count(*) from ledger.pouryakit_receipts where subscription = 'ledger.note-deposit'"));
        await Db.ExecuteAsync(ConnectionString, "update wolverine.wolverine_incoming_envelopes set status = 'Incoming', owner_id = 0 where received_at = 'local://ledger.note-deposit/'");
        await WaitUntilAsync("select count(*) = 1 from wolverine.wolverine_incoming_envelopes where received_at = 'local://ledger.note-deposit/' and status = 'Handled'");
        Assert.Equal(1, LedgerProbe.RunsOf(deposited));
    }

    [Fact]
    public async Task An_idempotent_command_that_changes_no_stream_keeps_its_key_and_replays_its_success()
    {
        var key = Guid.CreateVersion7();
        var host = await StartAsync();

        var first = await Ledger(host).SendAsync(new PingCommand(key), Context(requestId: "ping-1"), Token);
        var replayed = await Ledger(host).SendAsync(new PingCommand(key), Context(requestId: "ping-1"), Token);

        Assert.True(first.IsSuccess && replayed.IsSuccess);
        Assert.Equal(1, LedgerProbe.RunsOf(key));
        Assert.Equal(1, await CountAsync("select count(*) from ledger.pouryakit_idempotency_keys where request_id = 'ping-1'"));
    }
}
