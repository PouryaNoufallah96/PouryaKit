using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;

namespace PouryaKit.Messaging.Wolverine;

// A declaration of a Module's messages that belongs to no Module in a Host that composed Modules, or that names a Module
// the Host never composed, a Host without Modules included: publishing it, delivering it or scheduling it would run in no
// Module of the Host and fail only at its first use, once the Host serves. A Composition Root declares on its builder;
// the Host's composition names a Module it composes, and an application without Modules declares with no Module.
// Receive and ReceiveSnapshot belong to the Host and are not checked.
internal sealed class ModuleOwnershipCheck : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        foreach (var (module, declaration, failure, call, arguments) in Declarations(services))
        {
            if (module is null && composition.Modules.Count > 0)
            {
                yield return $"{declaration} is declared without a Module in a Host of Modules, so it runs in no Module and {failure} once the Host serves. Declare it in its Module's Composition Root with {call}({arguments}), or name the Module in the Host's composition with {call}({Named(arguments)}typeof(TheModule)).";
            }
            else if (module is not null && !composition.Modules.Contains(module))
            {
                var otherwise = composition.Modules.Count > 0
                    ? "name a Module the Host composes"
                    : $"declare it for the application, without a Module, with {call}({Application(arguments)})";
                yield return $"{declaration} is declared for {module.Name}, which this Host does not compose, so it runs in no Module of the Host and {failure} once the Host serves. Add {module.Name} with AddPouryaKitModules, or {otherwise}.";
            }
        }
    }

    private static string Named(string arguments) => arguments is "" ? "" : arguments + ", ";

    // Only a Module's builder infers the Feature assemblies, so the application's AddPouryaKitNotificationHandlers, the one
    // declaration whose Module form takes no argument, names them.
    private static string Application(string arguments) => arguments is "" ? "featureAssemblies" : arguments;

    // Each declaration with its Module, how to name it in a sentence, what would fail, and the call that declares it.
    private static IEnumerable<(Type? Module, string Declaration, string Failure, string Call, string Arguments)> Declarations(IServiceProvider services)
    {
        if (services.GetService<Subscriptions>() is { } subscriptions)
        {
            foreach (var (message, publication) in subscriptions.Publications)
            {
                yield return subscriptions.Snapshots.FirstOrDefault(snapshot => snapshot.Snapshot == message) is { } snapshot
                    ? (
                        publication.Module,
                        $"The Snapshot {snapshot.Snapshot.FullName} of {snapshot.Aggregate.FullName}",
                        "a commit that changes its aggregate would fail",
                        $"PublishSnapshot<{snapshot.Aggregate.Name}, {snapshot.Snapshot.Name}, {snapshot.Mapper.Name}>",
                        "wireName"
                    )
                    : (publication.Module, $"The Integration Event {message.FullName}", "publishing it would fail", $"Publish<{message.Name}>", "wireName");
            }

            foreach (var subscription in subscriptions.All)
            {
                yield return subscription.IsNotification
                    ? (
                        subscription.Module,
                        $"The Notification Handler {subscription.Handler.FullName}, subscribed as {subscription.Name},",
                        "its deliveries would fail",
                        "AddPouryaKitNotificationHandlers",
                        ""
                    )
                    : (
                        subscription.Module,
                        $"The subscription {subscription.Name} of {subscription.Handler.FullName}",
                        "its deliveries would fail",
                        $"Subscribe<{subscription.Message.Name}, {subscription.Handler.Name}>",
                        "subscriptionName"
                    );
            }
        }

        foreach (var declared in services.GetService<DelayedCommandDeclarations>()?.All ?? [])
        {
            yield return declared.Recurring is null
                ? (
                    declared.Module,
                    $"The delayed command {declared.Name}, {declared.Command.FullName},",
                    "scheduling it would fail",
                    $"AddPouryaKitDelayedCommand<{declared.Command.Name}>",
                    "name"
                )
                : (
                    declared.Module,
                    $"The recurring command {declared.Name}, {declared.Command.FullName},",
                    "its occurrences would fail",
                    $"AddPouryaKitRecurringCommand<{declared.Command.Name}>",
                    "name, systemActor, occurrence"
                );
        }
    }
}
