using M2.Host;
using M2.Ledger;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Messaging.Wolverine.EFCore;

namespace M2.Tests;

// Items 8 and 9: Ledger's Receipts on the Marten Unit of Work's session, recorded by the Kit's own delivery step before
// the handler runs. A concurrent duplicate comes from the inbox stale time: the welcome handler holds longer than it, so
// the store hands the delivery out again while the first run still holds its Receipt.
public sealed class ReceiptTests(Postgres postgres) : SpikeTest(postgres)
{
    private static readonly SpikeHostOptions StaleAfterHalfASecond = new()
    {
        Services = services => services.Configure<PouryaKitMessageStoreOptions>(store => store.InboxStaleTime = TimeSpan.FromMilliseconds(500)),
    };

    [Fact]
    public async Task A_concurrent_duplicate_delivery_waits_for_the_first_receipt_and_never_runs_its_handler()
    {
        var accountId = Guid.CreateVersion7();
        LedgerProbe.Hold(accountId, TimeSpan.FromSeconds(2));
        var host = await StartAsync(StaleAfterHalfASecond);

        await Ledger(host).SendAsync(new OpenAccountCommand(accountId, Guid.CreateVersion7()), Context(), Token);
        await WaitUntilAsync("select count(*) > 0 from pg_stat_activity where wait_event_type = 'Lock' and query like 'insert into ledger.pouryakit_receipts%'");
        await WaitUntilAsync("select count(*) = 0 from wolverine.wolverine_incoming_envelopes where status <> 'Handled'");

        Assert.Equal(1, LedgerProbe.RunsOf(accountId));
        Assert.Equal(1, await CountAsync("select count(*) from events.mt_events where stream_id = $1 and type = 'welcomed'", accountId));
        Assert.Equal(1, await CountAsync("select count(*) from ledger.pouryakit_receipts where subscription = 'ledger.welcome'"));
    }

    // The rejected option, one honest attempt: Receipts as Marten documents. A document is written only at the save, so
    // the duplicate finds no Receipt, runs its handler, and its save is refused; its retry then applies nothing.
    [Fact]
    public async Task With_document_receipts_a_concurrent_duplicate_runs_its_handler_and_is_refused_at_the_save()
    {
        var accountId = Guid.CreateVersion7();
        LedgerProbe.Hold(accountId, TimeSpan.FromSeconds(2));
        LedgerModule.DocumentReceipts = true;
        try
        {
            var host = await StartAsync(StaleAfterHalfASecond);

            await Ledger(host).SendAsync(new OpenAccountCommand(accountId, Guid.CreateVersion7()), Context(), Token);
            await WaitUntilAsync("select count(*) = 1 from events.mt_events where type = 'welcomed'");
            await WaitUntilAsync("select count(*) = 0 from wolverine.wolverine_incoming_envelopes where status <> 'Handled'");

            Assert.True(LedgerProbe.RunsOf(accountId) > 1);
            Assert.Equal(1, await CountAsync("select count(*) from events.mt_events where stream_id = $1 and type = 'welcomed'", accountId));
            Assert.Equal(1, await CountAsync("select count(*) from events.mt_doc_receiptdocument where id like 'ledger.welcome:%'"));
        }
        finally
        {
            LedgerModule.DocumentReceipts = false;
        }
    }

    [Fact]
    public async Task A_failed_notification_handler_rolls_its_receipt_back_with_its_stream_effect_and_its_integration_event()
    {
        var accountId = Guid.CreateVersion7();
        LedgerProbe.Fail(accountId);
        var host = await StartAsync();

        await Ledger(host).SendAsync(new OpenAccountCommand(accountId, Guid.CreateVersion7()), Context(), Token);
        await WaitUntilAsync("select count(*) = 1 from wolverine.wolverine_dead_letters where received_at = 'local://ledger.welcome/'");

        // Every attempt ran the handler: no failed attempt's Receipt survived to skip the next one.
        Assert.Equal(3, LedgerProbe.RunsOf(accountId));
        Assert.Equal(0, await CountAsync("select count(*) from events.mt_events where stream_id = $1 and type = 'welcomed'", accountId));
        Assert.Equal(0, await CountAsync("select count(*) from ledger.pouryakit_receipts"));
        Assert.Equal(0, await CountAsync("select count(*) from wolverine.wolverine_incoming_envelopes where received_at = 'local://catalog.account-opened/'"));
    }
}
