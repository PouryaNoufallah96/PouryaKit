using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Core;
using PouryaKit.Cqrs;

namespace PouryaKit.Messaging.Wolverine;

// A Notification Handler in the Feature assemblies of a Module the Host composed, or of the application's own services,
// that no AddPouryaKitNotificationHandlers subscribed: it would never run, and a Domain Event no other handler subscribes
// to would be dropped before its commit.
internal sealed class UnregisteredNotificationHandlersCheck : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        var subscribed = services.GetService<Subscriptions>()?.All.Where(subscription => subscription.IsNotification).Select(subscription => subscription.Handler).ToHashSet() ?? [];
        foreach (var module in composition.Modules.Append<Type?>(null))
        {
            List<Type> unsubscribed =
            [
                .. ImplementingTypes
                    .Candidates(typeof(INotificationHandler<>), composition.FeatureAssembliesOf(module))
                    .Where(handler => !handler.IsAbstract && !subscribed.Contains(handler)),
            ];
            if (unsubscribed.Count > 0)
            {
                var names = string.Join(", ", unsubscribed.Select(handler => handler.FullName));
                yield return module is null
                    ? $"The application's Notification Handlers {names} are declared by no AddPouryaKitNotificationHandlers, so they never run. Call AddPouryaKitNotificationHandlers(assembly) on the Host's services, naming their assembly."
                    : $"The Notification Handlers {names} of {module.Name} are declared by no AddPouryaKitNotificationHandlers, so they never run. Call AddPouryaKitNotificationHandlers() in its Composition Root.";
            }
        }
    }
}
