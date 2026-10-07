using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Messaging.Wolverine;

public static class IntegrationEventServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Declares that the Module whose Composition Root calls it on its builder (<see cref="IModuleServices"/>)
        /// publishes <typeparamref name="TIntegrationEvent"/> under <paramref name="wireName"/>; on the application's own
        /// services, in an application without Modules, the application does. The wire name is stored with every
        /// delivery of it and resolves its type when the delivery is read back, so it is stable: keep it when the type is
        /// renamed. One wire name is one version of one contract type: a new version is a new type under a new wire
        /// name, such as <c>ProductCreatedV2</c> under <c>catalog.product-created.v2</c>, published beside the old one
        /// while subscriptions move to it, and each subscription receives only the type it subscribed to; nothing converts one
        /// version into another. Throws here, at startup, when the type is not a concrete class or is a Domain
        /// Event, when the wire name is missing, invalid or equal ignoring case to another's, and when the type is already
        /// published under another name or by another Module. The Host then fails when it is built if the declaration
        /// belongs to no Module in a Host of Modules or to a Module the Host does not compose, and fails to start unless an
        /// Outbox writes Integration Events
        /// (<see cref="INotificationOutbox"/>), and when Wolverine would route another of its message types to this
        /// type's subscriptions, because that type's wire name, name or full name equals this type's full name, ignoring case.
        /// </summary>
        public IServiceCollection Publish<TIntegrationEvent>(string wireName)
            where TIntegrationEvent : class =>
            DeclarePublication(services, typeof(TIntegrationEvent), wireName, IModuleServices.ModuleOf(services));

        /// <summary>
        /// Declares, in the Host's composition, that <paramref name="module"/>, a Module's definition type, publishes
        /// <typeparamref name="TIntegrationEvent"/> under <paramref name="wireName"/>, as
        /// <see cref="Publish{TIntegrationEvent}(string)"/> on that Module's builder does. Throws, besides, when called on
        /// the builder of another Module.
        /// </summary>
        public IServiceCollection Publish<TIntegrationEvent>(string wireName, Type module)
            where TIntegrationEvent : class =>
            DeclarePublication(services, typeof(TIntegrationEvent), wireName, IModuleServices.ModuleOf(services, module));

        /// <summary>
        /// Declares that another Host publishes <typeparamref name="TIntegrationEvent"/> under <paramref name="wireName"/>,
        /// so this Host's subscriptions to it receive it from there, through a transport between Hosts such as
        /// <c>UsePouryaKitRabbitMq</c>. The Host's composition calls it, not a Module's Composition Root: whether the
        /// publishing Module runs in this Host is the Host's choice. The wire name is the one the publishing Module declares.
        /// Throws here, at startup, when the type is not a concrete class or is a Domain Event, when the wire name is
        /// missing, invalid or equal ignoring case to another's, when the type is already received under another name or as
        /// a Snapshot, and when a Module of this Host publishes it. Declaring it again adds nothing.
        /// </summary>
        public IServiceCollection Receive<TIntegrationEvent>(string wireName)
            where TIntegrationEvent : class
        {
            ArgumentNullException.ThrowIfNull(services);
            Subscriptions.In(services).Receive(typeof(TIntegrationEvent), wireName, isSnapshot: false);
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, StartupCheck>());
            return services;
        }

        /// <summary>
        /// Subscribes the Integration Event Handler <typeparamref name="THandler"/> of the Module whose Composition Root
        /// calls it on its builder (<see cref="IModuleServices"/>), or of the application on its own services, to
        /// <typeparamref name="TIntegrationEvent"/> under <paramref name="subscriptionName"/>: its own durable queue and
        /// half of its Receipts' key, so it is stable; keep it when the handler is renamed. Its deliveries run in that
        /// Module, so they reach its Unit of Work and its Receipts. The handler may be internal. Throws here, at startup,
        /// when the type is a Domain Event, when the handler is abstract, and when the name is missing, invalid, equal
        /// ignoring case to another subscription's, or the name of a queue Wolverine keeps for itself, or the same
        /// subscription belongs to another Module. Declaring the same subscription again in the same Module adds nothing.
        /// The name is kept lower-cased. The Host then fails when it is built if the declaration belongs to no Module in a
        /// Host of Modules or to a Module the Host does not compose, and fails to start when no Module of the Host
        /// publishes the type and no <c>Receive</c> declares it, when no Outbox writes Integration Events, or when the
        /// subscription's queue, as Wolverine built it, is not a durable application queue.
        /// </summary>
        public IServiceCollection Subscribe<TIntegrationEvent, THandler>(string subscriptionName)
            where TIntegrationEvent : class
            where THandler : class, IIntegrationEventHandler<TIntegrationEvent> =>
            DeclareSubscription<TIntegrationEvent, THandler>(services, subscriptionName, IModuleServices.ModuleOf(services));

        /// <summary>
        /// Subscribes, in the Host's composition, the Integration Event Handler <typeparamref name="THandler"/> of
        /// <paramref name="module"/>, a Module's definition type, as
        /// <see cref="Subscribe{TIntegrationEvent, THandler}(string)"/> on that Module's builder does: its deliveries run
        /// in <paramref name="module"/>. Throws, besides, when called on the builder of another Module.
        /// </summary>
        public IServiceCollection Subscribe<TIntegrationEvent, THandler>(string subscriptionName, Type module)
            where TIntegrationEvent : class
            where THandler : class, IIntegrationEventHandler<TIntegrationEvent> =>
            DeclareSubscription<TIntegrationEvent, THandler>(services, subscriptionName, IModuleServices.ModuleOf(services, module));
    }

    private static IServiceCollection DeclarePublication(IServiceCollection services, Type integrationEvent, string wireName, Type? module)
    {
        Subscriptions.In(services).Publish(integrationEvent, wireName, module);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, StartupCheck>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompositionCheck, ModuleOwnershipCheck>());
        return services;
    }

    private static IServiceCollection DeclareSubscription<TIntegrationEvent, THandler>(
        IServiceCollection services,
        string subscriptionName,
        Type? module
    )
        where TIntegrationEvent : class
        where THandler : class, IIntegrationEventHandler<TIntegrationEvent>
    {
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
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompositionCheck, ModuleOwnershipCheck>());
        return services;
    }
}
