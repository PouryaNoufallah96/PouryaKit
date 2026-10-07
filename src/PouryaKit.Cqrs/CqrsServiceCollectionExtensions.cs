using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PouryaKit.Core;

namespace PouryaKit.Cqrs;

public static class CqrsServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the dispatcher and every command handler, query handler, authorizer and Domain Event Handler in
        /// <paramref name="featureAssemblies"/>, internal types included, and the system <see cref="TimeProvider"/>
        /// unless one is registered: the dispatcher binds it as each Operation's <c>DomainClock</c>, so register a
        /// different one to change the time Operations see. Throws here, at startup, when a command or
        /// query has no handler or several, when an authorizer is not for one concrete command or query, when a Domain
        /// Event Handler handles several Domain Event types or an abstract one, or when a command, query, handler or
        /// authorizer is a generic type definition.
        /// </summary>
        public IServiceCollection AddPouryaKitCqrs(params Assembly[] featureAssemblies)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(featureAssemblies);

            services.TryAddScoped(provider => new Operation(() => provider.GetService<IReadingStateGuard>()));
            services.TryAddScoped<IReadingStateGuard>(provider => (provider.GetService<IUnitOfWork>() as IReadingStateGuard)!);
            services.TryAddScoped(OperationContextServiceProviderExtensions.Read);
            services.TryAddScoped<IDispatcher, Dispatcher>();
            services.TryAddSingleton(TimeProvider.System);

            // In declaration order, assembly by assembly, which is the order an operation's authorizers run in.
            var classes = ImplementingTypes
                .Candidates(typeof(object), featureAssemblies)
                .Where(type => !type.IsAbstract)
                .OrderBy(type => Array.IndexOf(featureAssemblies, type.Assembly))
                .ThenBy(type => type.MetadataToken)
                .ToList();
            var types = classes.Where(type => !type.IsGenericTypeDefinition).ToList();

            var handlers = types
                .Select(type => (Implementation: type, Service: HandlerBase(type)))
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
            var operations = types.Where(OperationTypes.IsOperation).Concat(handlersByOperation.Select(group => group.Key)).Distinct();
            List<string> problems =
            [
                .. classes
                    .Where(type =>
                        type.IsGenericTypeDefinition
                        && (
                            OperationTypes.IsOperation(type)
                            || HandlerBase(type) is not null
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

            services.TryAddScoped(provider => new DomainEventDrain(provider, provider.GetRequiredService<Operation>()));
            foreach (var (implementation, service) in domainEventHandlers)
            {
                services.TryAddEnumerable(ServiceDescriptor.Scoped(service, implementation));
            }

            return services;
        }

        /// <summary>
        /// Throws when a command or query whose handler is registered declares, with an <see cref="IRequiresAuthorizer"/>
        /// attribute such as Security's <c>[RequiresPermission]</c>, an authorizer of the Kit that is not registered for
        /// it, one line per operation: its check would never run. Another authorizer of the same operation does not count.
        /// It reads the registrations as they are, so call it on the Host's final ones, when the container is built:
        /// <c>AddPouryaKitModules</c> does, and a Host without Modules passes it to <c>ConfigureContainer</c>.
        /// </summary>
        public void EnsurePouryaKitDeclaredAuthorizers()
        {
            ArgumentNullException.ThrowIfNull(services);

            // ImplementationType throws on a keyed descriptor, and the dispatcher resolves handlers and authorizers unkeyed.
            var unkeyed = services.Where(descriptor => !descriptor.IsKeyedService).ToList();
            var registered = unkeyed
                .Where(descriptor => descriptor.ImplementationType is not null)
                .Select(descriptor => (descriptor.ServiceType, descriptor.ImplementationType!))
                .ToHashSet();
            List<string> problems =
            [
                .. unkeyed
                    .Select(descriptor => OperationTypes.HandledBy(descriptor.ServiceType))
                    .OfType<Type>()
                    .Distinct()
                    .SelectMany(operation =>
                        operation
                            .GetCustomAttributes(inherit: false)
                            .OfType<IRequiresAuthorizer>()
                            .Where(declared =>
                                !registered.Contains((typeof(IAuthorizer<>).MakeGenericType(operation), declared.AuthorizerOf(operation)))
                            )
                            .Select(declared =>
                                $"{operation.FullName} declares {declared.GetType().Name}, but its check is not registered, so it would run unchecked. Call {declared.Registration} with the assembly {operation.Assembly.GetName().Name} in the Composition Root that registers its handler."
                            )
                    ),
            ];
            if (problems.Count > 0)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
            }
        }
    }

    // A second call with the same assembly adds nothing; a different handler for an operation already handled is a
    // second handler.
    private static void AddHandler(IServiceCollection services, Type service, Type implementation)
    {
        var existing = services.FirstOrDefault(descriptor => descriptor.ServiceType == service);
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

    private static Type? HandlerBase(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (OperationTypes.HandledBy(current) is not null)
            {
                return current;
            }
        }

        return null;
    }

    // The dispatcher resolves an authorizer by the exact runtime type, so one for an open type parameter, a base type
    // or a marker interface would register and never run.
    private static bool IsConcreteOperation(Type type) =>
        !type.IsGenericParameter && !type.ContainsGenericParameters && !type.IsAbstract && OperationTypes.IsOperation(type);
}
