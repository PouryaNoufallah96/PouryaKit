using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace PouryaKit.Messaging.Wolverine;

public static class NotificationHandlerServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Declares the Notification Handlers of one Module: every <see cref="INotificationHandler{TDomainEvent}"/> in
        /// <paramref name="handlerAssembly"/>, internal types included, each subscribed under its subscription name to its
        /// Domain Event's wire name (<see cref="MessageNames"/>). Their deliveries run in <paramref name="module"/>, the
        /// Module's definition type, so they reach its Unit of Work. A Module's Composition Root calls it on its builder;
        /// an application without Modules calls it on its own services with no Module. Throws here, at startup, when the
        /// assembly has no Notification Handler, when a handler handles several Domain Event types, an abstract or a
        /// generic one, or is generic itself, and when a wire name or a subscription name is missing, invalid, equal
        /// ignoring case to another's, or, for a subscription, the name of a queue Wolverine keeps for itself. The Host
        /// then fails to start unless an Outbox writes the Notifications (<see cref="INotificationOutbox"/>) and every
        /// subscription's queue, as Wolverine built it, is a durable application queue.
        /// </summary>
        public IServiceCollection AddPouryaKitNotificationHandlers(Assembly handlerAssembly, Type? module = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(handlerAssembly);

            List<string> problems = [];
            List<Subscription> subscriptions = [];
            foreach (var type in handlerAssembly.GetTypes().Where(type => type is { IsClass: true, IsAbstract: false }))
            {
                var handled = type.GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(INotificationHandler<>))
                    .Select(i => i.GenericTypeArguments[0])
                    .ToList();
                if (handled.Count == 0)
                {
                    continue;
                }

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
                problems.Add($"{handlerAssembly.GetName().Name} has no Notification Handler.");
            }

            Subscriptions.In(services).Add(subscriptions, problems);
            foreach (var subscription in subscriptions)
            {
                services.TryAddScoped(subscription.Handler);
            }

            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, StartupCheck>());

            return services;
        }
    }
}
