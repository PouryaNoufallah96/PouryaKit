using Microsoft.EntityFrameworkCore;
using Wolverine.Persistence;

namespace S1.Tests;

/// <summary>Objective 1: the owned Unit of Work commits business rows and outbox rows together, for each Module's DbContext.</summary>
public sealed class AtomicCommitTests(PostgresServer server)
{
    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(3);

    public static TheoryData<ModuleName, EnvelopeMapping, TransactionMiddlewareMode> CommitCases()
    {
        var data = new TheoryData<ModuleName, EnvelopeMapping, TransactionMiddlewareMode>();
        foreach (var module in new[] { ModuleName.Catalog, ModuleName.Inventory })
        foreach (var mapping in new[] { EnvelopeMapping.Raw, EnvelopeMapping.MappedToStoreSchema })
        foreach (var mode in new[] { TransactionMiddlewareMode.Eager, TransactionMiddlewareMode.Lightweight })
            data.Add(module, mapping, mode);
        return data;
    }

    public static TheoryData<ModuleName, EnvelopeMapping> RollbackCases()
    {
        var data = new TheoryData<ModuleName, EnvelopeMapping>();
        foreach (var module in new[] { ModuleName.Catalog, ModuleName.Inventory })
        foreach (var mapping in new[] { EnvelopeMapping.Raw, EnvelopeMapping.MappedToStoreSchema })
            data.Add(module, mapping);
        return data;
    }

    [Theory]
    [MemberData(nameof(CommitCases))]
    public async Task Commit_writes_business_row_and_envelope_row_in_one_transaction(
        ModuleName module, EnvelopeMapping mapping, TransactionMiddlewareMode mode)
    {
        await using var db = await server.CreateDatabaseAsync();
        using var host = db.BuildHost(mode, mapping);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var driver = new ModuleDriver(host, db, module);
        var id = Guid.NewGuid();

        long businessInTx = -1, envelopesInTx = -1, businessOutside = -1, envelopesOutside = -1;
        string? businessXmin = null;
        DateTime businessCommittedAt = default;
        List<EnvelopeRow> envelopesAfterCommit = [];

        var outcome = await driver.RunAsync(id, hooks: new CommitHooks
        {
            AfterSaveBeforeCommit = async ctx =>
            {
                businessInTx = await ctx.Database
                    .SqlQueryRaw<long>($"select count(*) as \"Value\" from {driver.BusinessTable}").SingleAsync();
                envelopesInTx = await ctx.Database
                    .SqlQueryRaw<long>("select count(*) as \"Value\" from wolverine.wolverine_incoming_envelopes").SingleAsync();
                businessOutside = await driver.BusinessRowsAsync(id);
                envelopesOutside = await db.IncomingCountAsync();
            },
            AfterCommitBeforeFlush = async () =>
            {
                businessXmin = await driver.BusinessXminAsync(id);
                businessCommittedAt = await db.CommittedAtAsync(driver.BusinessTable, id);
                envelopesAfterCommit = await db.IncomingEnvelopesAsync();
            },
        });

        Assert.Equal(Outcome.Succeeded, outcome);
        // Saved, not committed: the Operation's own transaction holds both rows; another connection sees neither.
        Assert.Equal((1, 1), (businessInTx, envelopesInTx));
        Assert.Equal((0, 0), (businessOutside, envelopesOutside));
        // Committed: both rows visible, committed by one transaction (same commit timestamp).
        // A Notification bound for a local queue is stored as an *incoming* envelope; the outgoing table stays empty.
        var envelope = Assert.Single(envelopesAfterCommit);
        Assert.Equal(driver.MessageType, envelope.MessageType);
        Assert.Equal(businessCommittedAt, envelope.CommittedAt);
        Assert.Equal(0, await db.OutgoingCountAsync());
        // Raw: the envelope is inserted at publish time in the top-level transaction, the business row by
        // SaveChanges inside EF's automatic savepoint, so their xmin differ. Mapped: both are written by SaveChanges.
        if (mapping == EnvelopeMapping.Raw) Assert.NotEqual(businessXmin, envelope.Xmin);
        else Assert.Equal(businessXmin, envelope.Xmin);

        Assert.True(await Eventually.WaitAsync(async () => await db.DeliveriesAsync(id) == 1, DeliveryBudget));
    }

    [Theory]
    [MemberData(nameof(RollbackCases))]
    public async Task Exception_after_save_rolls_back_business_row_and_envelope_row(ModuleName module, EnvelopeMapping mapping)
    {
        await using var db = await server.CreateDatabaseAsync();
        using var host = db.BuildHost(mapping: mapping);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var driver = new ModuleDriver(host, db, module);
        var id = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.RunAsync(id, hooks: new CommitHooks
        {
            AfterSaveBeforeCommit = _ => throw new InvalidOperationException("failure after save"),
        }));

        await Task.Delay(QuietPeriod);
        Assert.Equal(0, await driver.BusinessRowsAsync(id));
        Assert.Equal(0, await db.IncomingCountAsync());
        Assert.Equal(0, await db.DeliveriesAsync(id));
    }

    [Theory]
    [MemberData(nameof(RollbackCases))]
    public async Task Failed_outcome_after_publish_leaves_no_business_row_and_no_envelope_row(ModuleName module, EnvelopeMapping mapping)
    {
        await using var db = await server.CreateDatabaseAsync();
        using var host = db.BuildHost(mapping: mapping);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var driver = new ModuleDriver(host, db, module);
        var id = Guid.NewGuid();

        var outcome = await driver.RunAsync(id, Outcome.Failed);

        Assert.Equal(Outcome.Failed, outcome);
        await Task.Delay(QuietPeriod);
        Assert.Equal(0, await driver.BusinessRowsAsync(id));
        Assert.Equal(0, await db.IncomingCountAsync());
        Assert.Equal(0, await db.DeliveriesAsync(id));
    }

    [Theory]
    [MemberData(nameof(RollbackCases))]
    public async Task Failed_save_leaves_no_envelope_row(ModuleName module, EnvelopeMapping mapping)
    {
        await using var db = await server.CreateDatabaseAsync();
        using var host = db.BuildHost(mapping: mapping);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var driver = new ModuleDriver(host, db, module);
        var id = Guid.NewGuid();
        await driver.SeedAsync(id);

        await Assert.ThrowsAsync<DbUpdateException>(() => driver.RunAsync(id));

        await Task.Delay(QuietPeriod);
        Assert.Equal(1, await driver.BusinessRowsAsync(id));
        Assert.Equal(0, await db.IncomingCountAsync());
        Assert.Equal(0, await db.DeliveriesAsync(id));
    }
}
