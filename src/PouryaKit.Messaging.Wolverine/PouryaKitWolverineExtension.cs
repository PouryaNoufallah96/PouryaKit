using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.ErrorHandling;

namespace PouryaKit.Messaging.Wolverine;

// Applied when Wolverine's options are first resolved, as the Host starts, after every Composition Root ran: so it sees
// the subscriptions of Modules added after UsePouryaKitWolverine, and checks what only the whole Host can tell.
internal sealed class PouryaKitWolverineExtension(
    Subscriptions subscriptions,
    IOptions<PouryaKitMessagingOptions> options,
    IServiceProviderIsService services
) : IWolverineExtension
{
    public void Configure(WolverineOptions wolverine)
    {
        List<string> problems =
        [
            .. subscriptions
                .All.Where(subscription =>
                    !subscription.IsNotification && !subscriptions.Publications.ContainsKey(subscription.Message)
                )
                .Select(subscription =>
                    $"{subscription.Handler.FullName} subscribes to {subscription.Message.FullName} under {subscription.Name}, but no Module of this Host publishes it, so it has no wire name. Declare Publish<{subscription.Message.Name}>(wireName, module) in the publishing Module's Composition Root."
                ),
        ];
        if (subscriptions.Publications.Count > 0 && !services.IsService(typeof(IIntegrationEventOutbox)))
        {
            problems.Add(
                "Modules of this Host publish Integration Events, but it has no Outbox to write them to. Run its deliveries with the message store of PouryaKit.Messaging.Wolverine.EFCore."
            );
        }

        if (problems.Count == 0)
        {
            problems.AddRange(WireNaming.Declare(subscriptions.Publications));
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
        }

        // One route per subscription, so one durable envelope per subscription, on the subscription's own queue.
        foreach (var subscription in subscriptions.All)
        {
            wolverine.PublishMessage(subscription.Message).ToLocalQueue(subscription.Name);
        }

        foreach (var messageType in subscriptions.MessageTypes)
        {
            wolverine.AddMessageHandler(messageType, new SubscriptionDelivery(subscriptions));
        }

        // A stored retry: an in-memory one would run again after InboxStaleTime handed the delivery out once more.
        wolverine.OnException<Exception>().ScheduleRetry(options.Value.RetryDelays).Then.MoveToErrorQueue();
    }
}
