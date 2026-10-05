using S2.Billing;
using S2.Host;
using S2.Inventory;
using S2.Kit.Harness;

namespace S2.Tests;

/// <summary>
/// ADR 0004's remedy for a stranded Notification is <c>InboxStaleTime</c>: every inbox row owned by a node and inserted
/// longer ago than the stale time is released (owner set to 0) and handed out again. A delivery waiting in an in-memory
/// retry is such a row. Option C, Inventory fails once, Billing succeeds; <c>InboxStaleTime</c> 3 s, 1 s polls.
/// </summary>
public sealed class StaleRetryTests(PostgresServer server)
{
    /// <summary>
    /// <c>RetryWithCooldown(10 s)</c>: the row stays <c>Incoming</c> and owned while the cooldown runs in memory, so it
    /// is released after about 3 s and handed out again; that run succeeds. When the cooldown ends, the first hand-out
    /// retries from memory and runs Inventory a third time, although the row is already Handled.
    /// </summary>
    [Fact]
    public async Task An_in_memory_retry_longer_than_inbox_stale_time_runs_the_handler_again_after_it_succeeded()
    {
        await using var db = await server.CreateDatabaseAsync();
        var productId = Guid.NewGuid();
        HandlerScript.Set(InventoryModule.HandlerName, productId, new Plan(FailureKind.FailedResult, Failures: 1));
        using var host = await db.StartHostAsync(new SpikeSettings(db.ConnectionString, DeliveryChoice.RoutedQueues,
            RetryCooldowns: [TimeSpan.FromSeconds(10)], InboxStaleTime: TimeSpan.FromSeconds(3)));

        await SpikeDatabase.PublishAsync(host, productId);

        Assert.True(await Eventually.WaitAsync(async () =>
            (await db.InvocationsAsync(InventoryModule.HandlerName, productId)).Contains("Succeeded"), TimeSpan.FromSeconds(9)));
        Assert.True(await Eventually.WaitAsync(async () =>
            (await db.InvocationsAsync(InventoryModule.HandlerName, productId)).Count == 3, TimeSpan.FromSeconds(15)));
        Assert.Equal(["FailedResult", "Succeeded", "Succeeded"], await db.InvocationsAsync(InventoryModule.HandlerName, productId));
        Assert.Equal(["Succeeded"], await db.InvocationsAsync(BillingModule.HandlerName, productId));
    }

    /// <summary>
    /// <c>ScheduleRetry(10 s)</c>: the failed delivery is written back as <c>Scheduled</c>, owner 0, with its attempt
    /// count, so the stale release (owner != 0) does not touch it. It runs once more, at the scheduled time.
    /// </summary>
    [Fact]
    public async Task A_persisted_retry_is_not_handed_out_again_by_inbox_stale_time()
    {
        await using var db = await server.CreateDatabaseAsync();
        var productId = Guid.NewGuid();
        HandlerScript.Set(InventoryModule.HandlerName, productId, new Plan(FailureKind.FailedResult, Failures: 1));
        using var host = await db.StartHostAsync(new SpikeSettings(db.ConnectionString, DeliveryChoice.RoutedQueues,
            RetryCooldowns: [TimeSpan.FromSeconds(10)], PersistedRetry: true, InboxStaleTime: TimeSpan.FromSeconds(3)));

        await SpikeDatabase.PublishAsync(host, productId);

        Assert.True(await Eventually.WaitAsync(async () =>
            (await db.IncomingAsync()).Any(e => e.Subscription == InventoryModule.Subscription && e.Status == "Scheduled"), TimeSpan.FromSeconds(5)));
        var scheduled = (await db.IncomingAsync()).Single(e => e.Subscription == InventoryModule.Subscription);
        Assert.Equal((0, 1), (scheduled.OwnerId, scheduled.Attempts));
        await Task.Delay(TimeSpan.FromSeconds(6));
        Assert.Equal(["FailedResult"], await db.InvocationsAsync(InventoryModule.HandlerName, productId));

        Assert.True(await Eventually.WaitAsync(async () =>
            (await db.InvocationsAsync(InventoryModule.HandlerName, productId)).Contains("Succeeded"), TimeSpan.FromSeconds(10)));
        await Task.Delay(TimeSpan.FromSeconds(5));
        Assert.Equal(["FailedResult", "Succeeded"], await db.InvocationsAsync(InventoryModule.HandlerName, productId));
    }

    /// <summary>
    /// <c>RetryWithCooldown(1 s)</c>: the whole ladder ends before the row's insert time is 3 s old, so nothing is
    /// released. The release compares the insert <c>timestamp</c>, which nothing updates, so what must stay below the
    /// stale time is the whole ladder plus handling, not each cooldown.
    /// </summary>
    [Fact]
    public async Task An_in_memory_retry_ladder_that_ends_before_inbox_stale_time_runs_the_handler_once_more()
    {
        await using var db = await server.CreateDatabaseAsync();
        var productId = Guid.NewGuid();
        HandlerScript.Set(InventoryModule.HandlerName, productId, new Plan(FailureKind.FailedResult, Failures: 1));
        using var host = await db.StartHostAsync(new SpikeSettings(db.ConnectionString, DeliveryChoice.RoutedQueues,
            RetryCooldowns: [TimeSpan.FromSeconds(1)], InboxStaleTime: TimeSpan.FromSeconds(3)));

        await SpikeDatabase.PublishAsync(host, productId);

        Assert.True(await Eventually.WaitAsync(async () =>
            (await db.InvocationsAsync(InventoryModule.HandlerName, productId)).Contains("Succeeded"), TimeSpan.FromSeconds(5)));
        await Task.Delay(TimeSpan.FromSeconds(6));
        Assert.Equal(["FailedResult", "Succeeded"], await db.InvocationsAsync(InventoryModule.HandlerName, productId));
    }
}
