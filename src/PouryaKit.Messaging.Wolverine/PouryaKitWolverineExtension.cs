using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.ErrorHandling;

namespace PouryaKit.Messaging.Wolverine;

// Applied when Wolverine's options are first resolved, after every Composition Root ran, so it sees the subscriptions
// of Modules added after UsePouryaKitWolverine.
internal sealed class PouryaKitWolverineExtension(
    NotificationSubscriptions subscriptions,
    IOptions<PouryaKitMessagingOptions> options
) : IWolverineExtension
{
    public void Configure(WolverineOptions wolverine)
    {
        // One route per subscription, so one durable envelope per subscription, on the subscription's own queue.
        foreach (var subscription in subscriptions.All)
        {
            wolverine.PublishMessage(subscription.DomainEvent).ToLocalQueue(subscription.Name);
        }

        foreach (var domainEvent in subscriptions.DomainEvents)
        {
            wolverine.AddMessageHandler(domainEvent, new NotificationDelivery(subscriptions));
        }

        // A stored retry: an in-memory one would run again after InboxStaleTime handed the delivery out once more.
        wolverine.OnException<Exception>().ScheduleRetry(options.Value.RetryDelays).Then.MoveToErrorQueue();
    }
}
