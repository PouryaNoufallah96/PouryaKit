using Wolverine;

namespace PouryaKit.Messaging.Wolverine;

// Applied when Wolverine's options are first resolved, after every Composition Root ran: one durable queue per delayed
// command, which Wolverine's scheduled-message support holds a command in until it is due. The retry policy of
// PouryaKitWolverineExtension covers its deliveries too.
internal sealed class DelayedCommandExtension(DelayedCommandDeclarations declarations, Subscriptions subscriptions)
    : IWolverineExtension
{
    public void Configure(WolverineOptions wolverine)
    {
        List<string> problems =
        [
            .. declarations
                .All.Where(declared => subscriptions.All.Any(subscription => subscription.Name == declared.Name))
                .Select(declared =>
                    $"The delayed command {declared.Command.FullName} and a subscription have the name {declared.Name}, ignoring case; each needs a queue of its own."
                ),
        ];
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
        }

        // One handler per type: AddMessageHandler gives the instance that type's chain.
        foreach (var declared in declarations.All)
        {
            wolverine.PublishMessage(declared.Command).ToLocalQueue(declared.Name);
            wolverine.AddMessageHandler(declared.Command, new DelayedCommandDelivery(declarations));
        }
    }
}
