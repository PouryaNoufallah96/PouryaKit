using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PouryaKit.Core;
using PouryaKit.Cqrs;

namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// Declares a Module's Notification Handlers, part of the Messaging Capability, in its Composition Root or, naming the
/// Module, in the Host's composition.
/// </summary>
public static class NotificationHandlerServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Declares the Notification Handlers of one Module: every <see cref="INotificationHandler{TDomainEvent}"/> in
        /// <paramref name="featureAssemblies"/>, internal types included, each subscribed under its subscription name to
        /// its Domain Event's wire name (<see cref="MessageNames"/>). A Module's Composition Root calls it on its builder
        /// (<see cref="IModuleServices"/>), where, named with no assembly, it scans the assembly of the Module's
        /// definition, and named assemblies replace it; their deliveries run in that Module, so they reach its Unit of
        /// Work. An application without Modules calls it on its own services, naming the assembly. Throws here, at
        /// startup, when the assemblies have no Notification Handler, when a handler handles several Domain Event types,
        /// an abstract or a generic one, or is generic itself, and when a wire name or a subscription name is missing,
        /// invalid, equal ignoring case to another's, or, for a subscription, the name of a queue Wolverine keeps for
        /// itself or the same subscription declared for another Module. Repeating a declaration for the same Module adds
        /// nothing. The Host then fails when it is built if the declaration belongs to no Module in a Host of Modules or to
        /// a Module the Host does not compose, if that Module, or the application, registers no Unit of Work, or unless an
        /// Outbox writes the Notifications (<see cref="IOutbox"/>), and fails to start unless every
        /// subscription's queue, as Wolverine built it, is a durable application queue.
        /// </summary>
        public IServiceCollection AddPouryaKitNotificationHandlers(params Assembly[] featureAssemblies) =>
            DeclareNotificationHandlers(
                services,
                IModuleServices.FeatureAssembliesOf(services, featureAssemblies),
                IModuleServices.ModuleOf(services)
            );

        /// <summary>
        /// Declares, in the Host's composition, the Notification Handlers of <paramref name="module"/>, a Module's
        /// definition type, as <c>AddPouryaKitNotificationHandlers</c> on that Module's builder does:
        /// those in <paramref name="featureAssemblies"/>, or in the assembly of the Module's definition when it names
        /// none. Throws, besides, when called on the builder of another Module.
        /// </summary>
        public IServiceCollection AddPouryaKitNotificationHandlers(Type module, params Assembly[] featureAssemblies) =>
            DeclareNotificationHandlers(services, IModuleServices.FeatureAssembliesOf(services, featureAssemblies, module), module);
    }

    private static IServiceCollection DeclareNotificationHandlers(IServiceCollection services, Assembly[] assemblies, Type? module)
    {
        List<string> problems = [];
        List<Subscription> subscriptions = [];
        // In declaration order, assembly by assembly: of two handlers whose names clash, the one declared first keeps its name.
        var candidates = ImplementingTypes
            .Candidates(typeof(INotificationHandler<>), assemblies)
            .Where(type => !type.IsAbstract)
            .OrderBy(type => Array.IndexOf(assemblies, type.Assembly))
            .ThenBy(type => type.MetadataToken);
        foreach (var type in candidates)
        {
            var handled = type.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(INotificationHandler<>))
                .Select(i => i.GenericTypeArguments[0])
                .ToList();
            if (type.IsGenericTypeDefinition || handled.Count > 1 || handled[0].IsAbstract || handled[0].IsGenericType)
            {
                problems.Add(
                    $"{type.FullName} is a Notification Handler of {string.Join(", ", handled.Select(e => e.Name))}; a Notification Handler is a non-generic class that handles one concrete, non-generic Domain Event type."
                );
                continue;
            }

            subscriptions.Add(
                Subscription.OfNotificationHandler(MessageNames.SubscriptionNameOf(type, handled[0]), handled[0], type, module)
            );
        }

        if (subscriptions.Count == 0 && problems.Count == 0)
        {
            problems.Add(
                assemblies is []
                    ? "AddPouryaKitNotificationHandlers names no assembly, and on the Host's own services it infers none. Name the assembly of the Notification Handlers."
                    : $"{string.Join(", ", assemblies.Select(assembly => assembly.GetName().Name))} has no Notification Handler."
            );
        }

        Subscriptions.In(services).Add(subscriptions, problems);
        foreach (var subscription in subscriptions)
        {
            services.TryAddScoped(subscription.Handler);
        }

        DurableQueuesCheck.AddTo(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompositionCheck, ModuleOwnershipCheck>());
        return services;
    }
}
