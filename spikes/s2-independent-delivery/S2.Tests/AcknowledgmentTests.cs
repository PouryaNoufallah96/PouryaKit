using Microsoft.Extensions.DependencyInjection;
using S2.Billing;
using S2.Catalog;
using S2.Host;
using S2.Inventory;
using Wolverine;

namespace S2.Tests;

public sealed class AcknowledgmentTests(PostgresServer server)
{
    private const string Envelope = "kit.delivery." + CatalogModule.ProductCreatedWireName;
    private static readonly string[] SubscriptionQueues = [$"local://{BillingModule.Subscription}/", $"local://{InventoryModule.Subscription}/"];
    private static readonly string[] OneQueue = [$"local://{Envelope}/", $"local://{Envelope}/"];

    public static TheoryData<DeliveryChoice, MessageIdentity, string, string[]> Cases => new()
    {
        { DeliveryChoice.SeparatedQueues, MessageIdentity.IdOnly, CatalogModule.ProductCreatedWireName, SubscriptionQueues },
        { DeliveryChoice.SeparatedQueues, MessageIdentity.IdAndDestination, CatalogModule.ProductCreatedWireName, SubscriptionQueues },
        { DeliveryChoice.DeliveryEnvelope, MessageIdentity.IdOnly, Envelope, OneQueue },
        { DeliveryChoice.DeliveryEnvelope, MessageIdentity.IdAndDestination, Envelope, OneQueue },
    };

    /// <summary>
    /// One published message becomes one durable envelope row per subscription, written in the Unit of Work's commit,
    /// each with its own envelope id, and each marked Handled on its own. Option A tells the rows apart by queue
    /// (<c>received_at</c>); Option B's rows share one queue and carry the subscription in the body.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Each_handler_is_acknowledged_by_its_own_envelope_row(DeliveryChoice delivery, MessageIdentity identity, string messageType, string[] queues)
    {
        await using var db = await server.CreateDatabaseAsync();
        var productId = Guid.NewGuid();
        using var host = await db.StartHostAsync(new SpikeSettings(db.ConnectionString, delivery, Identity: identity));

        List<EnvelopeRow> committed = [];
        List<DateTime> envelopeCommits = [];
        DateTime productCommit = default;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await CatalogModule.CreateProductAsync(scope.ServiceProvider.GetRequiredService<S2.Kit.UnitOfWork<CatalogDbContext>>(), productId, async () =>
            {
                committed = await db.IncomingAsync();
                envelopeCommits = await db.ListAsync("select pg_xact_commit_timestamp(xmin) from wolverine.wolverine_incoming_envelopes", r => r.GetDateTime(0));
                productCommit = (await db.ListAsync("select pg_xact_commit_timestamp(xmin) from catalog.products where id = $1", r => r.GetDateTime(0), productId)).Single();
            });
        }

        Assert.Equal([BillingModule.Subscription, InventoryModule.Subscription], committed.Select(e => e.Subscription));
        Assert.Equal(queues, committed.Select(e => e.ReceivedAt));
        Assert.All(committed, e => Assert.Equal("Incoming", e.Status));
        Assert.All(committed, e => Assert.Equal(messageType, e.MessageType));
        Assert.Equal(2, committed.Select(e => e.Id).Distinct().Count());
        Assert.All(envelopeCommits, t => Assert.Equal(productCommit, t));

        Assert.True(await Eventually.WaitAsync(async () =>
            (await db.IncomingAsync()).All(e => e.Status == "Handled"), TimeSpan.FromSeconds(15)));
        Assert.Equal(committed.Select(e => e.Id), (await db.IncomingAsync()).Select(e => e.Id));

        var primaryKey = await db.ListAsync("""
            select a.attname from pg_index i
            join pg_attribute a on a.attrelid = i.indrelid and a.attnum = any(i.indkey)
            where i.indrelid = 'wolverine.wolverine_incoming_envelopes'::regclass and i.indisprimary
            order by a.attname
            """, r => r.GetString(0));
        Assert.Equal(identity == MessageIdentity.IdOnly ? ["id"] : ["id", "received_at"], primaryKey);
    }
}
