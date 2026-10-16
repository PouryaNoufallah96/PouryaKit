using M2.Ledger;

namespace M2.Tests;

// Item 16: Marten's conjoined tenancy keyed by the Kit's tenant. The Marten Unit of Work opens its session for the
// Operation's tenant; a delivery restores the tenant from the envelope's headers, so the welcome is appended under it.
public sealed class TenancyTests(Postgres postgres) : SpikeTest(postgres)
{
    [Fact]
    public async Task A_stream_is_kept_and_delivered_under_the_operations_tenant_and_no_other_tenant_reads_it()
    {
        var accountId = Guid.CreateVersion7();
        var host = await StartAsync();

        await Ledger(host).SendAsync(new OpenAccountCommand(accountId, Guid.CreateVersion7()), Context("acme"), Token);
        await WaitUntilAsync("select count(*) = 1 from events.mt_events where stream_id = $1 and type = 'welcomed'", accountId);

        Assert.Equal(["acme", "acme"], (await Db.RowsAsync(ConnectionString, "select tenant_id from events.mt_events where stream_id = $1", accountId)).Select(row => (string)row[0]!));
        Assert.True((await Ledger(host).QueryAsync(new GetAccountQuery(accountId), Context("acme"), Token)).IsSuccess);
        Assert.False((await Ledger(host).QueryAsync(new GetAccountQuery(accountId), Context("globex"), Token)).IsSuccess);
        Assert.False((await Ledger(host).QueryAsync(new GetAccountQuery(accountId), Context(), Token)).IsSuccess);
    }

    // Conjoined streams are keyed by tenant and id, so one id is a stream of its own under each tenant; an Operation with
    // no tenant writes under Marten's default tenant.
    [Fact]
    public async Task One_stream_id_is_a_stream_of_its_own_under_each_tenant_and_no_tenant_is_martens_default()
    {
        var accountId = Guid.CreateVersion7();
        var host = await StartAsync();
        await Ledger(host).SendAsync(new OpenAccountCommand(accountId, Guid.CreateVersion7()), Context("acme"), Token);

        var globex = await Ledger(host).SendAsync(new OpenAccountCommand(accountId, Guid.CreateVersion7()), Context("globex"), Token);
        var none = await Ledger(host).SendAsync(new OpenAccountCommand(Guid.CreateVersion7(), Guid.CreateVersion7()), Context(), Token);

        Assert.True(globex.IsSuccess);
        Assert.Equal(["acme", "globex"], (await Db.RowsAsync(ConnectionString, "select tenant_id from events.mt_events where stream_id = $1 and type = 'account_opened' order by tenant_id", accountId)).Select(row => (string)row[0]!));
        Assert.True(none.IsSuccess);
        Assert.Equal(1, await CountAsync("select count(*) from events.mt_events where tenant_id = '*DEFAULT*' and type = 'account_opened'"));
    }
}
