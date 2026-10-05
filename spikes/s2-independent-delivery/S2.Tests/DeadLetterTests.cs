using S2.Billing;
using S2.Host;
using S2.Inventory;
using S2.Kit;
using S2.Kit.Harness;

namespace S2.Tests;

public sealed class DeadLetterTests(PostgresServer server)
{
    public static TheoryData<DeliveryChoice, string> Cases => new()
    {
        { DeliveryChoice.RoutedQueues, "catalog.product-created.v1" },
        { DeliveryChoice.SeparatedQueues, "catalog.product-created.v1" },
        { DeliveryChoice.DeliveryEnvelope, "kit.delivery.catalog.product-created.v1" },
    };

    /// <summary>
    /// With <c>RetryWithCooldown(200 ms, 200 ms).Then.MoveToErrorQueue()</c>, a handler that keeps returning a failed
    /// Result is tried three times and its envelope moves to <c>wolverine_dead_letters</c>; it is never marked Handled.
    /// The other handler's envelope is Handled once. Setting <c>replayable</c> on the dead letter puts it back in the
    /// inbox, and only the failed handler runs again.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Exhausted_retries_set_the_message_aside_for_that_handler_only(DeliveryChoice delivery, string messageType)
    {
        await using var db = await server.CreateDatabaseAsync();
        var productId = Guid.NewGuid();
        HandlerScript.Set(InventoryModule.HandlerName, productId, new Plan(FailureKind.FailedResult, Failures: 3));
        using var host = await db.StartHostAsync(delivery);

        await SpikeDatabase.PublishAsync(host, productId);

        Assert.True(await Eventually.WaitAsync(async () => (await db.DeadLettersAsync()).Count == 1, TimeSpan.FromSeconds(15)));
        await Task.Delay(TimeSpan.FromSeconds(1));
        var deadLetter = Assert.Single(await db.DeadLettersAsync());
        Assert.Equal(InventoryModule.Subscription, deadLetter.Subscription);
        Assert.Equal(messageType, deadLetter.MessageType);
        Assert.Equal(typeof(HandlerFailedException).FullName, deadLetter.ExceptionType);
        Assert.False(deadLetter.Replayable);
        Assert.Equal(["FailedResult", "FailedResult", "FailedResult"], await db.InvocationsAsync(InventoryModule.HandlerName, productId));
        Assert.Equal(["Succeeded"], await db.InvocationsAsync(BillingModule.HandlerName, productId));
        var incoming = Assert.Single(await db.IncomingAsync());
        Assert.Equal((BillingModule.Subscription, "Handled"), (incoming.Subscription, incoming.Status));

        // Inspection and replay: the fourth invocation succeeds.
        await db.CountAsync("update wolverine.wolverine_dead_letters set replayable = true where id = $1", deadLetter.Id);

        Assert.True(await Eventually.WaitAsync(async () =>
            (await db.InvocationsAsync(InventoryModule.HandlerName, productId)).Contains("Succeeded"), TimeSpan.FromSeconds(20)));
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Empty(await db.DeadLettersAsync());
        Assert.Equal(["Succeeded"], await db.InvocationsAsync(BillingModule.HandlerName, productId));
    }
}
