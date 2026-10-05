using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Domain;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Modularity;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

/// <summary>
/// <c>InboxStaleTime</c> hands out again every delivery a Host has held longer than it, counted from when the delivery
/// was stored (ADR 0004). A failed delivery waits for its retry in the store, held by no Host, so the release passes it
/// by; but a retry that runs after the stale time is already past it, and is handed out again while it runs.
/// </summary>
public sealed class InboxStaleTimeTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private static readonly TimeSpan StaleTime = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task A_delivery_waiting_for_its_retry_is_not_handed_out_again_and_one_that_succeeded_never_runs_again()
    {
        var circulated = new NoteToCirculateDomainEvent(Unique());
        Circulation.Fail(Circulation.Ledger, circulated.Text, runs: 1);
        await using var host = await StartStaleHostAsync();

        await Fixture.WaitForDeliveriesAsync(() => CirculateAsync(host, circulated), TestContext.Current.CancellationToken);

        // The retry waited out its whole delay, twice the stale time, and nothing handed it out before.
        var ledger = DeliveryProbes.AttemptsOf(Circulation.Ledger, circulated.Id);
        Assert.Equal(2, ledger.Count);
        Assert.True(ledger[1] - ledger[0] >= RetryDelay, $"The retry ran {(ledger[1] - ledger[0]).TotalMilliseconds} ms after the failure.");
        // A bounded wait for something that must not happen: past the stale time and several recovery polls, neither
        // handled delivery is handed out again.
        await Task.Delay(StaleTime + TestDeliveryServiceCollectionExtensions.PollingTime * 5, TestContext.Current.CancellationToken);
        Assert.Equal(2, DeliveryProbes.AttemptsOf(Circulation.Ledger, circulated.Id).Count);
        Assert.Single(DeliveryProbes.AttemptsOf(Circulation.Archive, circulated.Id));
        Assert.Equal((1L, 1L), (await CopiesAsync(Circulation.Ledger, circulated.Text), await CopiesAsync(Circulation.Archive, circulated.Text)));
    }

    [Fact]
    public async Task A_retry_that_runs_after_the_stale_time_is_handed_out_again_while_it_runs_and_its_receipt_keeps_one_effect()
    {
        var circulated = new NoteToCirculateDomainEvent(Unique());
        Circulation.Fail(Circulation.Ledger, circulated.Text, runs: 1);
        await using var host = await StartStaleHostAsync();

        await CirculateAsync(host, circulated);
        await Fixture.WaitUntilAsync(
            $"exists (select 1 from wolverine.wolverine_incoming_envelopes where received_at = 'local://{Circulation.Ledger}/' and status = 'Scheduled')",
            "The ledger's first attempt never failed into its retry."
        );
        using (var hold = DeliveryProbes.HoldAfterReceipt(Circulation.Ledger))
        {
            // The retry, stored longer ago than the stale time, records its Receipt and waits. Nothing in the test
            // touches its envelope: the store releases it as stale and hands it out again, and that copy waits on the
            // held Receipt.
            await hold.Held;
            await Fixture.WaitUntilAsync(
                "exists (select 1 from pg_stat_activity where wait_event_type = 'Lock' and query like '%pouryakit_receipts%')",
                "No copy of the held retry was handed out."
            );
        }

        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);
        Assert.True(DeliveryProbes.AttemptsOf(Circulation.Ledger, circulated.Id).Count >= 3);
        // The failed attempt and the held retry ran the handler; every copy found the retry's Receipt.
        Assert.Equal(2, Circulation.RunsOf(Circulation.Ledger, circulated.Text).Count);
        Assert.Equal(1L, await CopiesAsync(Circulation.Ledger, circulated.Text));
        Assert.Equal(
            [["Handled"]],
            await Fixture.RowsAsync($"select status from wolverine.wolverine_incoming_envelopes where received_at = 'local://{Circulation.Ledger}/'")
        );
        Assert.Empty(await Fixture.DeadLettersAsync(TestContext.Current.CancellationToken));
    }

    private async Task<WebApplication> StartStaleHostAsync()
    {
        var host = AppFixture.ComposeHost(
            "Development",
            Fixture.ConnectionString,
            Fixture.Logs,
            builder =>
            {
                builder.Services.Configure<PouryaKitMessageStoreOptions>(store => store.InboxStaleTime = StaleTime);
                builder.Services.Configure<PouryaKitMessagingOptions>(messaging => messaging.RetryDelays = [RetryDelay]);
            }
        );
        await HostFixture.StartHostAsync(host, TestContext.Current.CancellationToken);
        return host;
    }

    private static Task<Result> CirculateAsync(WebApplication host, NoteToCirculateDomainEvent circulated) =>
        host.Services.GetRequiredService<IGatewayProcessor<StubModule>>()
            .SendAsync(new WriteNoteRaisingCommand(circulated.Text, [circulated]), TestContext.Current.CancellationToken);

    private Task<long> CopiesAsync(string subscription, string text) =>
        Fixture.ScalarAsync<long>($"select count(*) from stub.notes where text = '{Circulation.CopyOf(subscription, text)}'");

    private static string Unique() => $"note {Guid.CreateVersion7()}";
}
