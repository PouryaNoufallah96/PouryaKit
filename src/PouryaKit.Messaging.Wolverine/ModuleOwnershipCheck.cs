using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;

namespace PouryaKit.Messaging.Wolverine;

// A declaration of a Module's messages that belongs to no Module, in a Host that composed Modules: publishing it,
// delivering it or scheduling it would run in no Module's Operation and fail only at its first use, once the Host serves.
// A Composition Root declares on its builder; the Host's composition names the Module. Receive and ReceiveSnapshot
// belong to the Host and are not checked.
internal sealed class ModuleOwnershipCheck : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        if (composition.Modules.Count == 0)
        {
            yield break;
        }

        if (services.GetService<Subscriptions>() is { } subscriptions)
        {
            foreach (var (message, _) in subscriptions.Publications.Where(publication => publication.Value.Module is null))
            {
                yield return subscriptions.Snapshots.FirstOrDefault(snapshot => snapshot.Snapshot == message) is { } snapshot
                    ? Unowned(
                        $"The Snapshot {snapshot.Snapshot.FullName} of {snapshot.Aggregate.FullName}",
                        "a commit that changes its aggregate would fail",
                        $"PublishSnapshot<{snapshot.Aggregate.Name}, {snapshot.Snapshot.Name}, {snapshot.Mapper.Name}>",
                        "wireName"
                    )
                    : Unowned($"The Integration Event {message.FullName}", "publishing it would fail", $"Publish<{message.Name}>", "wireName");
            }

            foreach (var subscription in subscriptions.All.Where(subscription => subscription.Module is null))
            {
                yield return subscription.IsNotification
                    ? Unowned(
                        $"The Notification Handler {subscription.Handler.FullName}, subscribed as {subscription.Name},",
                        "its deliveries would fail",
                        "AddPouryaKitNotificationHandlers",
                        ""
                    )
                    : Unowned(
                        $"The subscription {subscription.Name} of {subscription.Handler.FullName}",
                        "its deliveries would fail",
                        $"Subscribe<{subscription.Message.Name}, {subscription.Handler.Name}>",
                        "subscriptionName"
                    );
            }
        }

        foreach (var declared in services.GetService<DelayedCommandDeclarations>()?.All.Where(declared => declared.Module is null) ?? [])
        {
            yield return declared.Recurring is null
                ? Unowned(
                    $"The delayed command {declared.Name}, {declared.Command.FullName},",
                    "scheduling it would fail",
                    $"AddPouryaKitDelayedCommand<{declared.Command.Name}>",
                    "name"
                )
                : Unowned(
                    $"The recurring command {declared.Name}, {declared.Command.FullName},",
                    "its occurrences would fail",
                    $"AddPouryaKitRecurringCommand<{declared.Command.Name}>",
                    "name, systemActor, occurrence"
                );
        }
    }

    // The two forms of a Module's declaration: on its builder, without the Module, and in the Host's composition.
    private static string Unowned(string declaration, string failure, string call, string arguments) =>
        $"{declaration} is declared without a Module in a Host of Modules, so it runs in no Module and {failure} once the Host serves. Declare it in its Module's Composition Root with {call}({arguments}), or name the Module in the Host's composition with {call}({(arguments is "" ? "" : arguments + ", ")}typeof(TheModule)).";
}
