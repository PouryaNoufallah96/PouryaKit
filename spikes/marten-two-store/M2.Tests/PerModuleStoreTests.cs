using M2.Catalog;
using M2.Host;
using M2.Ledger;
using M2.Marten;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Modularity;

namespace M2.Tests;

// Item 11: in one Host, Ledger's Receipts and idempotency keys go to its Marten tables and Catalog's to its EF tables,
// each keyed by its Module and resolved through the Operation's Module, whether the resolvers are registered before the
// Kit's registrations, after them, or twice.
public sealed class PerModuleStoreTests(Postgres postgres) : SpikeTest(postgres)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Each_module_keeps_its_receipts_and_idempotency_keys_in_its_own_store_whatever_the_registration_order(bool routersFirst, bool twice)
    {
        var accountId = Guid.CreateVersion7();
        var host = await StartAsync(
            new SpikeHostOptions { RoutersFirst = routersFirst, Services = twice ? services => services.UseSpikeModuleStoreRouters() : null }
        );
        var catalog = host.Services.GetRequiredService<IGatewayProcessor<CatalogModule>>();

        var opened = await Ledger(host).SendAsync(new OpenAccountCommand(accountId, Guid.CreateVersion7()), Context(requestId: "open-1"), Token);
        var replayed = await Ledger(host).SendAsync(new OpenAccountCommand(accountId, Guid.CreateVersion7()), Context(requestId: "open-1"), Token);
        var touched = await catalog.SendAsync(new TouchCommand(), Context(requestId: "touch-1"), Token);
        await WaitUntilAsync("select count(*) = 1 from catalog.pouryakit_receipts where subscription = 'catalog.account-opened'");

        Assert.True(opened.IsSuccess && replayed.IsSuccess && touched.IsSuccess);
        Assert.Equal(1, await CountAsync("select count(*) from events.mt_events where type = 'account_opened'"));
        Assert.Equal(["open-1"], (await Db.RowsAsync(ConnectionString, "select request_id from ledger.pouryakit_idempotency_keys")).Select(row => (string)row[0]!));
        Assert.Equal(["touch-1"], (await Db.RowsAsync(ConnectionString, "select request_id from catalog.pouryakit_idempotency_keys")).Select(row => (string)row[0]!));
        Assert.Equal(
            ["ledger.account-opened.v1#published", "ledger.welcome"],
            (await Db.RowsAsync(ConnectionString, "select subscription from ledger.pouryakit_receipts order by subscription")).Select(row => (string)row[0]!)
        );
        Assert.Equal(["catalog.account-opened"], (await Db.RowsAsync(ConnectionString, "select subscription from catalog.pouryakit_receipts")).Select(row => (string)row[0]!));
    }
}
