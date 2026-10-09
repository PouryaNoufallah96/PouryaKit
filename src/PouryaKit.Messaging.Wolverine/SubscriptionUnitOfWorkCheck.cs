using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;

namespace PouryaKit.Messaging.Wolverine;

// Each delivery of a subscription is a command of its Module (Deliver), run in that Module's Unit of Work, where its
// Receipt is recorded: a Module that subscribes but declares no data would fail every delivery before its handler runs.
// A Module that handles a command of its own is checked as it is composed, and one that only answers queries needs no
// Unit of Work. A subscription of no Module in a Host of Modules, or of a Module the Host never composed, is
// ModuleOwnershipCheck's.
internal sealed class SubscriptionUnitOfWorkCheck : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        foreach (var owned in services.GetRequiredService<Subscriptions>().All.GroupBy(subscription => subscription.Module))
        {
            var module = owned.Key;
            var misowned = module is null ? composition.Modules.Count > 0 : !composition.Modules.Contains(module);
            if (misowned || HasUnitOfWork(composition, module))
            {
                continue;
            }

            var names = string.Join(", ", owned.Select(subscription => subscription.Name).Order(StringComparer.Ordinal));
            yield return module is null
                ? $"The application has the subscriptions {names} but registers no Unit of Work, so each of their deliveries would fail before its handler runs. Register the Unit of Work of a persistence package, for example with UsePouryaKitPostgres."
                : $"{module.Name} has the subscriptions {names} but registers no Unit of Work, so each of their deliveries would fail before its handler runs. Declare its data in its Composition Root, for example with UsePouryaKitPostgres.";
        }
    }

    // A Module's own is keyed by its definition type (ModuleBuilder); the application's is the one unkeyed.
    private static bool HasUnitOfWork(HostComposition composition, Type? module) =>
        composition.Registrations.Any(descriptor =>
            descriptor.ServiceType == typeof(IUnitOfWork)
            && (module is null ? !descriptor.IsKeyedService : descriptor.IsKeyedService && Equals(descriptor.ServiceKey, module))
        );
}
