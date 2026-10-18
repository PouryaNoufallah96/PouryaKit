using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Billing;
using Billing.Quotes.Features.OpenQuote;
using Ledger;
using Ledger.Accounts.Errors;
using Ledger.Accounts.Events.DomainEvents;
using Ledger.Accounts.Events.Notifications;
using Ledger.Accounts.Features.GetAccount;
using Ledger.Accounts.Features.OpenAccount;
using Ledger.Accounts.Features.PostEntry;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Modularity;
using PouryaKit.Persistence;
using PouryaKit.Testing;
using static ModularMonolith.Tests.Fixtures.ProblemResponses;

namespace ModularMonolith.Tests;

// Ticket #132: the Proving Sample's Stream Module Ledger, on the Marten packages beside the EF Core Modules, through its
// Gateway and over HTTP against PostgreSQL. An account's opening is activated by Ledger's Notification Handler, which
// publishes AccountOpenedV1 to Billing, which opens the account's welcome quote. A test waits for those deliveries
// before it posts an entry, which would otherwise race the activation's append to the same stream.
public sealed class LedgerTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private const string BillingSubscription = "billing.account-opened";

    private static readonly string Activation = MessageNames.SubscriptionNameOf(
        typeof(ActivateAccountNotificationHandler),
        typeof(AccountOpenedDomainEvent)
    );

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IGatewayProcessor<LedgerModule> Ledger => Fixture.Gateway<LedgerModule>();

    [Fact]
    public async Task Entries_append_to_an_account_through_ledgers_gateway_and_its_projection_folds_them_for_its_tenant_only()
    {
        var account = Guid.CreateVersion7();

        var (opened, _) = await Fixture.WaitForDeliveriesAsync(() => Ledger.SendAsync(new OpenAccountCommand(account, "Ada"), Context(), Token), Token);
        Assert.True((await Ledger.SendAsync(new PostEntryCommand(account, 10m), Context(), Token)).IsSuccess);
        Assert.True((await Ledger.SendAsync(new PostEntryCommand(account, -3m), Context(), Token)).IsSuccess);
        var read = await Ledger.QueryAsync(new GetAccountQuery(account), Context(), Token);

        Assert.Equal(account, opened.Value);
        Assert.Equal(new AccountDto(account, "Ada", 7m, IsActive: true, Version: 4), read.Value);
        Assert.Equal(
            [
                ["account_opened_domain_event", AppFixture.TestTenant],
                ["account_activated_domain_event", AppFixture.TestTenant],
                ["entry_posted_domain_event", AppFixture.TestTenant],
                ["entry_posted_domain_event", AppFixture.TestTenant],
            ],
            await Fixture.RowsAsync($"select type, tenant_id from events.mt_events where stream_id = '{account}' order by version")
        );
        Assert.Equal(AccountErrors.NotFound, Assert.Single((await Ledger.QueryAsync(new GetAccountQuery(account), Context("other"), Token)).Errors));
    }

    [Fact]
    public async Task Over_http_an_account_opens_at_its_location_takes_an_entry_and_reads_and_a_second_opening_is_the_kits_conflict()
    {
        var account = Guid.CreateVersion7();
        var client = Fixture.Client("ada", AppFixture.TestTenant);

        var opened = await client.PostAsJsonAsync("/ledger/accounts", new { accountId = account, holder = "Ada" }, Token);
        await Fixture.WaitUntilDeliveredAsync(Token);
        var posted = await client.PostAsJsonAsync($"/ledger/accounts/{account}/entries", new { amount = 25.5m }, Token);
        var read = await client.GetFromJsonAsync<JsonElement>($"/ledger/accounts/{account}", Token);
        var again = await client.PostAsJsonAsync("/ledger/accounts", new { accountId = account, holder = "Grace" }, Token);

        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        Assert.Equal($"/ledger/accounts/{account}", opened.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.NoContent, posted.StatusCode);
        Assert.Equal(
            ("Ada", 25.5m, true, 3L),
            (read.GetProperty("holder").GetString(), read.GetProperty("balance").GetDecimal(), read.GetProperty("isActive").GetBoolean(), read.GetProperty("version").GetInt64())
        );
        Assert.Equal(
            $$"""[{"code":"{{UnitOfWorkErrors.ConcurrencyConflict.Code}}","message":"{{UnitOfWorkErrors.ConcurrencyConflict.Message}}"}]""",
            (await ProblemAsync(again, HttpStatusCode.Conflict)).GetProperty("errors").GetRawText()
        );
        Assert.Equal(1L, await Fixture.ScalarAsync<long>($"select count(*) from events.mt_events where stream_id = '{account}' and type = 'account_opened_domain_event'"));
    }

    // Two commits publish, each read right after it, before anything is sent: the command's holds the account's opening
    // and the envelope of its Notification; the Notification Handler's holds the activation, its Receipt and the envelope
    // of the Integration Event to Billing.
    [Fact]
    public async Task An_opened_account_reaches_billing_from_commits_that_hold_each_stream_effect_with_its_receipt_and_envelope()
    {
        var account = Guid.CreateVersion7();
        List<object[]> committed = [];
        AppFixture.StreamCommits.OnCommit = async () =>
            committed.AddRange(await Fixture.RowsAsync("select received_at, pg_xact_commit_timestamp(xmin) from wolverine.wolverine_incoming_envelopes"));
        Result<Guid> opened;
        IReadOnlyList<Delivery> deliveries;
        try
        {
            (opened, deliveries) = await Fixture.WaitForDeliveriesAsync(() => Ledger.SendAsync(new OpenAccountCommand(account, "Ada"), Context(), Token), Token);
        }
        finally
        {
            AppFixture.StreamCommits.OnCommit = null;
        }

        Assert.True(opened.IsSuccess);
        Assert.Contains(new Delivery(BillingSubscription, "ledger.account-opened.v1", DeadLettered: false), deliveries);
        var (openingCommitted, activationCommitted) = (await CommittedAsync(account, "account_opened_domain_event"), await CommittedAsync(account, "account_activated_domain_event"));
        Assert.Equal(
            activationCommitted,
            await Fixture.ScalarAsync<DateTime>($"select pg_xact_commit_timestamp(xmin) from ledger.pouryakit_receipts where subscription = '{Activation}'")
        );
        Assert.Contains(committed, row => (string)row[0] == $"local://{Activation}/" && (DateTime)row[1] == openingCommitted);
        Assert.Contains(committed, row => (string)row[0] == $"local://{BillingSubscription}/" && (DateTime)row[1] == activationCommitted);
        // Billing's quote, its expiry and its Receipt, in one commit.
        Assert.Equal(
            [["Open", "Scheduled", true]],
            await Fixture.RowsAsync(
                $"""
                select q.state, e.status, pg_xact_commit_timestamp(q.xmin) = pg_xact_commit_timestamp(r.xmin)
                from billing.quotes q, wolverine.wolverine_incoming_envelopes e, billing.pouryakit_receipts r
                where q.id = '{account}' and e.received_at = 'local://billing.expire-quote/' and r.subscription = '{BillingSubscription}'
                """
            )
        );
    }

    // Billing already has a quote of the account's id, so its handler's quote fails the delivery's commit, every time.
    [Fact]
    public async Task A_failed_receiving_handler_leaves_no_receipt_and_no_effect()
    {
        var account = Guid.CreateVersion7();
        await Fixture.Gateway<BillingModule>().SendAsync(new OpenQuoteCommand(account, TimeSpan.FromHours(1)), Token);
        await Fixture.WaitUntilDeliveredAsync(Token);

        var (opened, deliveries) = await Fixture.WaitForDeliveriesAsync(() => Ledger.SendAsync(new OpenAccountCommand(account, "Ada"), Context(), Token), Token);

        Assert.True(opened.IsSuccess);
        Assert.Contains(new Delivery(BillingSubscription, "ledger.account-opened.v1", DeadLettered: true), deliveries);
        Assert.Equal(0L, await Fixture.ScalarAsync<long>($"select count(*) from billing.pouryakit_receipts where subscription = '{BillingSubscription}'"));
        Assert.Equal(
            1L,
            await Fixture.ScalarAsync<long>("select count(*) from wolverine.wolverine_incoming_envelopes where received_at = 'local://billing.expire-quote/'")
        );
    }

    private Task<DateTime> CommittedAsync(Guid account, string type) =>
        Fixture.ScalarAsync<DateTime>($"select pg_xact_commit_timestamp(xmin) from events.mt_events where stream_id = '{account}' and type = '{type}'");

    private static OperationContext Context(string tenant = AppFixture.TestTenant) => new(GatewayTestExtensions.TestActor, tenant);
}
