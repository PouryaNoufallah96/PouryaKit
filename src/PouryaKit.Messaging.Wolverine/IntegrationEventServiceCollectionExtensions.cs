using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PouryaKit.Domain;

namespace PouryaKit.Messaging.Wolverine;

public static class IntegrationEventServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Declares that <paramref name="module"/>, the Module's definition type, publishes
        /// <typeparamref name="TIntegrationEvent"/> under <paramref name="wireName"/>. The wire name is stored with every
        /// delivery of it and resolves its type when the delivery is read back, so it is stable: keep it when the type is
        /// renamed. One wire name is one version of one contract type: a new version is a new type under a new wire
        /// name, such as <c>ProductCreatedV2</c> under <c>catalog.product-created.v2</c>, published beside the old one
        /// while subscribers move, and each subscription receives only the type it subscribed to; nothing converts one
        /// version into another. A Module's Composition Root calls it on its builder; an application without Modules calls it on its
        /// own services with no Module. Throws here, at startup, when the type is not a concrete class or is a Domain
        /// Event, when the wire name is missing, invalid or equal ignoring case to another's, and when the type is already
        /// published under another name or by another Module. The Host then fails to start unless an Outbox writes Integration Events
        /// (<see cref="INotificationOutbox"/>), and when Wolverine would route another of its message types to this
        /// type's subscriptions, because that type's wire name, name or full name equals this type's full name, ignoring case.
        /// </summary>
        public IServiceCollection Publish<TIntegrationEvent>(string wireName, Type? module = null)
            where TIntegrationEvent : class
        {
            ArgumentNullException.ThrowIfNull(services);
            Subscriptions.In(services).Publish(typeof(TIntegrationEvent), wireName, module);
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, StartupCheck>());
            return services;
        }

        /// <summary>
        /// Subscribes the Integration Event Handler <typeparamref name="THandler"/> of <paramref name="module"/>, the
        /// Module's definition type, to <typeparamref name="TIntegrationEvent"/> under
        /// <paramref name="subscriptionName"/>: its own durable queue and half of its Receipts' key, so it is stable;
        /// keep it when the handler is renamed. Its deliveries run in <paramref name="module"/>, so they reach its Unit of
        /// Work and its Receipts. The handler may be internal. Throws here, at startup, when the type is a Domain Event,
        /// when the handler is abstract, and when the name is missing, invalid, equal ignoring case to another
        /// subscription's, or the name of a queue Wolverine keeps for itself, or the same subscription belongs to another
        /// Module. Declaring the same subscription again in the same Module adds nothing. The name is kept lower-cased.
        /// The Host then fails to start when no Module of the Host publishes the type, when no Outbox writes Integration Events, or
        /// when the subscription's queue, as Wolverine built it, is not a durable application queue.
        /// </summary>
        public IServiceCollection Subscribe<TIntegrationEvent, THandler>(string subscriptionName, Type? module = null)
            where TIntegrationEvent : class
            where THandler : class, IIntegrationEventHandler<TIntegrationEvent>
        {
            ArgumentNullException.ThrowIfNull(services);
            List<string> problems = [];
            if (typeof(TIntegrationEvent).IsAssignableTo(typeof(DomainEvent)))
            {
                problems.Add(
                    $"{typeof(THandler).FullName} subscribes to the Domain Event {typeof(TIntegrationEvent).FullName}; a Notification Handler reacts to a Domain Event, and an Integration Event Handler to an Integration Event."
                );
            }

            if (typeof(THandler).IsAbstract)
            {
                problems.Add($"{typeof(THandler).FullName} is abstract, so the subscription {subscriptionName} has no handler to run.");
            }

            Subscriptions
                .In(services)
                .Add(
                    [Subscription.OfIntegrationEventHandler(subscriptionName, typeof(TIntegrationEvent), typeof(THandler), module)],
                    problems
                );
            services.TryAddScoped<THandler>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, StartupCheck>());
            return services;
        }
    }
}
