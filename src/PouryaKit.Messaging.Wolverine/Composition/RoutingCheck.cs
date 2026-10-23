using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;

namespace PouryaKit.Messaging.Wolverine;

// What the Host's routes would be, read from its final declarations when it is built, before anything runs: every
// subscription's message has a wire name, every type received from another Host has a subscription, no message type is
// routed where another type goes, and no delayed command shares a subscription's queue. Only a Host whose routes pass
// declares its wire names for the process (WireNaming), all or none, and Wolverine routes by them once its options are
// built (PouryaKitWolverineExtension). Names declared stay the process's, as those of a Host that started and stopped do,
// even when another check then fails the build.
internal sealed class RoutingCheck : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        var subscriptions = services.GetRequiredService<Subscriptions>();
        var wireNames = subscriptions.WireNames;
        List<string> problems =
        [
            .. subscriptions
                .All.Where(subscription => !subscription.IsNotification && !wireNames.ContainsKey(subscription.Message))
                .Select(subscription =>
                    $"{subscription.Handler.FullName} subscribes to {subscription.Message.FullName} under {subscription.Name}, but no Module of this Host publishes it, so it has no wire name. Declare Publish<{subscription.Message.Name}>(wireName), or PublishSnapshot for a Snapshot, in the publishing Module's Composition Root; when another Host publishes it, declare Receive<{subscription.Message.Name}>(wireName), or ReceiveSnapshot for a Snapshot, in this Host's composition."
                ),
            .. subscriptions
                .ReceivedTypes.Except(subscriptions.MessageTypes)
                .Select(received =>
                    $"{received.FullName} is declared received from another Host as {wireNames[received]}, but no subscription or Saga of this Host handles it, so nothing would receive it. Subscribe<{received.Name}, THandler>(subscriptionName) in the Composition Root of the Module that receives it, or remove the {(subscriptions.IsSnapshot(received) ? "ReceiveSnapshot" : "Receive")} declaration."
                ),
            .. subscriptions.RoutingCollisions(),
            .. services
                .GetRequiredService<DelayedCommandDeclarations>()
                .All.Where(declared => subscriptions.All.Any(subscription => subscription.Name == declared.Name))
                .Select(declared =>
                    $"The delayed command {declared.Command.FullName} and a subscription have the name {declared.Name}, ignoring case; each needs a queue of its own."
                ),
        ];
        return problems.Count > 0 ? problems : WireNaming.Declare(wireNames, subscriptions.ReceivedTypes);
    }
}
