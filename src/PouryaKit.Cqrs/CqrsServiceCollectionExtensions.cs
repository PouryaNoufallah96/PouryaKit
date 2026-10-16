using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PouryaKit.Core;

namespace PouryaKit.Cqrs;

/// <summary>
/// Composes the Commands, queries and validation Capability on a Module's or a Host's services: the dispatcher, and the command handlers, query
/// handlers, authorizers and Domain Event Handlers of the Features.
/// </summary>
public static class CqrsServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the dispatcher and every command handler, query handler, authorizer and Domain Event Handler in
        /// <paramref name="featureAssemblies"/>, internal types included, and the system <see cref="TimeProvider"/>
        /// unless one is registered: the dispatcher binds it as each Operation's <c>DomainClock</c>, so register a
        /// different one to change the time Operations see. Named with no assembly on a Module's builder
        /// (<see cref="IModuleServices"/>), it scans the assembly of the Module's definition, where a Module's Features
        /// live; named assemblies replace it, in their order. On the Host's own services with no assembly, it registers the
        /// dispatcher alone. Throws here, while the Host is composed, when a command or
        /// query has no handler or several, when an authorizer is not for one concrete command or query, when a Domain
        /// Event Handler handles several Domain Event types or an abstract Domain Event type (an abstract handler class is
        /// skipped), or when a command, query, handler or
        /// authorizer is a generic type definition.
        /// </summary>
        public IServiceCollection AddPouryaKitCqrs(params Assembly[] featureAssemblies)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(featureAssemblies);

            var assemblies = IModuleServices.FeatureAssembliesOf(services, featureAssemblies);
            AddDispatcher(services);

            // In declaration order, assembly by assembly, which is the order an operation's authorizers run in.
            var classes = ImplementingTypes
                .Candidates(typeof(object), assemblies)
                .Where(type => !type.IsAbstract)
                .OrderBy(type => Array.IndexOf(assemblies, type.Assembly))
                .ThenBy(type => type.MetadataToken)
                .ToList();
            var types = classes.Where(type => !type.IsGenericTypeDefinition).ToList();

            var handlers = types
                .Select(type => (Implementation: type, Service: OperationTypes.HandlerBase(type)))
                .Where(handler => handler.Service is not null)
                .ToList();
            var authorizers = types
                .SelectMany(type => AuthorizerInterfaces(type).Select(service => (Implementation: type, Service: service)))
                .ToList();
            var domainEventHandlers = types
                .SelectMany(type =>
                    DomainEventHandlerInterfaces(type).Select(service => (Implementation: type, Service: service))
                )
                .ToList();

            var handlersByOperation = handlers.ToLookup(handler => handler.Service!.GenericTypeArguments[0]);
            var operations = OperationTypes.Dispatchable(types);
            List<string> problems =
            [
                .. classes
                    .Where(type =>
                        type.IsGenericTypeDefinition
                        && (
                            OperationTypes.IsOperation(type)
                            || OperationTypes.HandlerBase(type) is not null
                            || AuthorizerInterfaces(type).Any()
                            || DomainEventHandlerInterfaces(type).Any()
                        )
                    )
                    .Select(type =>
                        $"{type.FullName} is generic; generic commands, queries, handlers and authorizers are not supported."
                    ),
                .. operations
                    .Select(operation => (Operation: operation, Count: handlersByOperation[operation].Count()))
                    .Where(operation => operation.Count != 1)
                    .Select(operation =>
                        $"{operation.Operation.FullName} has {operation.Count} handlers; exactly one is required."
                    ),
                .. authorizers
                    .Where(authorizer => !IsConcreteOperation(authorizer.Service.GenericTypeArguments[0]))
                    .Select(authorizer =>
                        $"{authorizer.Implementation.FullName} is an authorizer, but not for one concrete command or query; the dispatcher would never run it."
                    ),
                .. domainEventHandlers
                    .GroupBy(handler => handler.Implementation)
                    .Where(handler => handler.Count() > 1)
                    .Select(handler =>
                        $"{handler.Key.FullName} handles {handler.Count()} Domain Event types; a Domain Event Handler handles one."
                    ),
                // The drain runs the handlers of a Domain Event's exact type, so one for an abstract type never runs.
                .. domainEventHandlers
                    .Where(handler => handler.Service.GenericTypeArguments[0].IsAbstract)
                    .Select(handler =>
                        $"{handler.Implementation.FullName} handles {handler.Service.GenericTypeArguments[0].Name}, which is abstract; the drain would never run it."
                    ),
            ];
            if (problems.Count > 0)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
            }

            foreach (var (implementation, service) in handlers)
            {
                AddHandler(services, service!, implementation);
            }

            foreach (var (implementation, service) in authorizers)
            {
                services.TryAddEnumerable(ServiceDescriptor.Scoped(service, implementation));
            }

            foreach (var (implementation, service) in domainEventHandlers)
            {
                services.TryAddEnumerable(ServiceDescriptor.Scoped(service, implementation));
            }

            return services;
        }
    }

    // The dispatcher and what each Operation's scope needs, registered once whatever calls it: AddPouryaKitCqrs, with or
    // without Feature assemblies, and the Host's AddPouryaKitCompositionChecks.
    internal static void AddDispatcher(IServiceCollection services)
    {
        services.TryAddScoped(provider => new Operation(() => provider.GetService<IReadingStateGuard>()));
        services.TryAddScoped<IReadingStateGuard>(provider => (provider.GetService<IUnitOfWork>() as IReadingStateGuard)!);
        services.TryAddScoped(OperationContextServiceProviderExtensions.Read);
        services.TryAddScoped<IDispatcher, Dispatcher>();
        // A Unit of Work resolves it in a factory, which container validation cannot see, so it comes with the dispatcher
        // even when no Composition Root calls AddPouryaKitCqrs.
        services.TryAddScoped(provider => new DomainEventDrain(provider, provider.GetRequiredService<Operation>()));
        services.TryAddSingleton(TimeProvider.System);
        // Every Kit Host reaches the dispatcher, so every Kit Host that starts runs the runner, which fails when the
        // Host never installed AddPouryaKitCompositionChecks: no Kit Host is silently without its checks.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, CompositionCheckRunner>());
        // Run by the Host's AddPouryaKitCompositionChecks when its container is built.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompositionCheck, DeclaredAuthorizersCheck>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompositionCheck, QueryCacheCheck>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompositionCheck, UnitOfWorkCheck>());
    }

    // A second call with the same assembly adds nothing; a different handler for an operation already handled is a
    // second handler. The dispatcher resolves handlers unkeyed, so a keyed service of a handler's type is no handler;
    // its descriptor's ImplementationType is null, which would read as a second one.
    private static void AddHandler(IServiceCollection services, Type service, Type implementation)
    {
        var existing = services.FirstOrDefault(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == service);
        if (existing is null)
        {
            services.AddScoped(service, implementation);
        }
        else if (existing.ImplementationType != implementation)
        {
            throw new InvalidOperationException(
                $"{service.GenericTypeArguments[0].FullName} has 2 handlers; exactly one is required."
            );
        }
    }

    private static IEnumerable<Type> AuthorizerInterfaces(Type type) =>
        type.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAuthorizer<>));

    private static IEnumerable<Type> DomainEventHandlerInterfaces(Type type) =>
        type.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDomainEventHandler<>));

    // The dispatcher resolves an authorizer by the exact runtime type, so one for an open type parameter, a base type
    // or a marker interface would register and never run.
    private static bool IsConcreteOperation(Type type) =>
        !type.IsGenericParameter && !type.ContainsGenericParameters && !type.IsAbstract && OperationTypes.IsOperation(type);
}
