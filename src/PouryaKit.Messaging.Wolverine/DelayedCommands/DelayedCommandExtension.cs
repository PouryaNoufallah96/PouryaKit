using Wolverine;

namespace PouryaKit.Messaging.Wolverine;

// Applied when Wolverine's options are first resolved, after every Composition Root ran: one durable queue per delayed
// command, which Wolverine's scheduled-message support holds a command in until it is due. The retry policy of
// PouryaKitWolverineExtension covers its deliveries too, and RoutingCheck refused a name a subscription has.
internal sealed class DelayedCommandExtension(DelayedCommandDeclarations declarations) : IWolverineExtension
{
    public void Configure(WolverineOptions wolverine)
    {
        // One handler per type: AddMessageHandler gives the instance that type's chain.
        foreach (var declared in declarations.All)
        {
            wolverine.PublishMessage(declared.Command).ToLocalQueue(declared.Name);
            wolverine.AddMessageHandler(declared.Command, new DelayedCommandDelivery(declarations));
        }
    }
}
