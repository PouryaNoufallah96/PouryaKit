using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.ErrorHandling;

namespace PouryaKit.Messaging.Wolverine;

// Applied when Wolverine's options are first resolved, after every Composition Root ran: so it routes the subscriptions
// of Modules added after UsePouryaKitWolverine too. RoutingCheck refused a Host whose routes are wrong when it was built.
internal sealed class PouryaKitWolverineExtension(
    Subscriptions subscriptions,
    IOptions<PouryaKitMessagingOptions> options
) : IWolverineExtension
{
    public void Configure(WolverineOptions wolverine)
    {
        // One route per subscription, so one durable envelope per subscription, on the subscription's own queue.
        foreach (var subscription in subscriptions.All)
        {
            wolverine.PublishMessage(subscription.Message).ToLocalQueue(subscription.Name);
        }

        foreach (var messageType in subscriptions.MessageTypes)
        {
            wolverine.AddMessageHandler(messageType, new SubscriptionDelivery(subscriptions));
        }

        // A stored retry, written back as held by no instance: a restart neither loses nor repeats its wait (ADR 0006).
        wolverine
            .OnException<Exception>()
            .ScheduleRetry(options.Value.RetryDelays)
            .Then.MoveToErrorQueue();
    }
}
