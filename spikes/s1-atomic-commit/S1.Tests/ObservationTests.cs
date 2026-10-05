using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Runtime;

namespace S1.Tests;

/// <summary>Behaviours plan section F lists as inferred, observed against the pinned Wolverine.</summary>
public sealed class ObservationTests(PostgresServer server)
{
    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task One_message_store_in_the_wolverine_schema_serves_both_module_DbContexts()
    {
        await using var db = await server.CreateDatabaseAsync();
        using var host = db.BuildHost();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var catalogId = Guid.NewGuid();
        var inventoryId = Guid.NewGuid();
        var envelopes = new List<EnvelopeRow>();

        await new ModuleDriver(host, db, ModuleName.Catalog).RunAsync(catalogId,
            hooks: new CommitHooks { AfterCommitBeforeFlush = async () => envelopes.AddRange(await db.IncomingEnvelopesAsync()) });
        await new ModuleDriver(host, db, ModuleName.Inventory).RunAsync(inventoryId,
            hooks: new CommitHooks { AfterCommitBeforeFlush = async () => envelopes.Add((await db.IncomingEnvelopesAsync()).Single(e => !envelopes.Any(x => x.Id == e.Id))) });

        // Wolverine's tables exist once, in schema "wolverine", and nowhere in the Modules' schemas.
        var tableSchemas = await db.ScalarAsync<string>(
            "select string_agg(distinct table_schema, ',') from information_schema.tables where table_name like 'wolverine%'");
        Assert.Equal("wolverine", tableSchemas);

        // Default local routing: one durable local queue per message type, named after the type's full name in lower case.
        Assert.Collection(envelopes,
            e => Assert.Equal(("local://s1.productcreated/", "Incoming"), (e.ReceivedAt, e.Status)),
            e => Assert.Equal(("local://s1.stockitemcreated/", "Incoming"), (e.ReceivedAt, e.Status)));

        // Envelopes written through the outbox before the flush are owned by the node that wrote them.
        var node = host.Services.GetRequiredService<IWolverineRuntime>().Options.Durability.AssignedNodeNumber;
        Assert.All(envelopes, e => Assert.Equal(node, e.OwnerId));

        Assert.True(await Eventually.WaitAsync(async () =>
            await db.DeliveriesAsync(catalogId) == 1 && await db.DeliveriesAsync(inventoryId) == 1, DeliveryBudget));
    }

    [Fact]
    public async Task Publish_through_the_plain_message_bus_escapes_the_unit_of_work_and_is_delivered_after_rollback()
    {
        await using var db = await server.CreateDatabaseAsync();
        using var host = db.BuildHost();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var driver = new ModuleDriver(host, db, ModuleName.Catalog);
        var id = Guid.NewGuid();
        var deliveredBeforeCommit = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.RunAsync(id, publishThroughPlainBus: true,
            hooks: new CommitHooks
            {
                AfterSaveBeforeCommit = async _ =>
                {
                    deliveredBeforeCommit = await Eventually.WaitAsync(async () => await db.DeliveriesAsync(id) == 1, DeliveryBudget);
                    throw new InvalidOperationException("failure after save");
                },
            }));

        // Without an enrolled transaction, publish stores the envelope on its own connection and sends at once:
        // the handler has run while the Operation's transaction is still open, and the rollback cannot undo it.
        Assert.True(deliveredBeforeCommit);
        Assert.Equal(0, await driver.BusinessRowsAsync(id));
        Assert.Equal("Handled", (await db.IncomingEnvelopesAsync()).Single().Status);
    }

    [Fact]
    public async Task Flushing_the_outbox_after_a_rollback_delivers_a_message_whose_business_row_was_rolled_back()
    {
        await using var db = await server.CreateDatabaseAsync();
        using var host = db.BuildHost();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var driver = new ModuleDriver(host, db, ModuleName.Catalog);
        var id = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.RunAsync(id, hooks: new CommitHooks
        {
            AfterSaveBeforeCommit = _ => throw new InvalidOperationException("failure after save"),
            FlushEvenAfterRollback = true,
        }));

        // Rollback removes the envelope row, but the outbox still holds the envelope in memory and sends it.
        Assert.True(await Eventually.WaitAsync(async () => await db.DeliveriesAsync(id) == 1, DeliveryBudget));
        Assert.Equal(0, await driver.BusinessRowsAsync(id));
        Assert.Equal(0, await db.CountAsync("select count(*) from wolverine.wolverine_incoming_envelopes where status = 'Incoming'"));
    }

    [Theory]
    [InlineData(ModuleName.Catalog)]
    [InlineData(ModuleName.Inventory)]
    public async Task Envelope_storage_mapped_without_the_store_schema_fails_the_commit(ModuleName module)
    {
        await using var db = await server.CreateDatabaseAsync();
        using var host = db.BuildHost(mapping: EnvelopeMapping.MappedToModuleSchema);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var driver = new ModuleDriver(host, db, module);
        var id = Guid.NewGuid();

        var error = await Assert.ThrowsAnyAsync<Exception>(() => driver.RunAsync(id));

        var schema = module == ModuleName.Catalog ? "catalog" : "inventory";
        Assert.Contains($"{schema}.wolverine_incoming_envelopes", error.ToString());
        await Task.Delay(QuietPeriod);
        Assert.Equal(0, await driver.BusinessRowsAsync(id));
        Assert.Equal(0, await db.DeliveriesAsync(id));
    }
}
