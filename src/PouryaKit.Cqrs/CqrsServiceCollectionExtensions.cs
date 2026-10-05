using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PouryaKit.Cqrs;

public static class CqrsServiceCollectionExtensions
{
    private static readonly Type[] HandlerDefinitions =
    [
        typeof(CommandHandler<>),
        typeof(CommandHandler<,>),
        typeof(QueryHandler<,>),
    ];

    /// <summary>
    /// Registers the dispatcher and every command handler, query handler and authorizer in
    /// <paramref name="featureAssemblies"/>, internal types included. Throws here, at startup, when a command or query
    /// has no handler or several.
    /// </summary>
    public static IServiceCollection AddPouryaKitCqrs(
        this IServiceCollection services,
        params Assembly[] featureAssemblies
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(featureAssemblies);

        services.TryAddScoped<Operation>();
        services.TryAddScoped<IDispatcher, Dispatcher>();

        var types = featureAssemblies
            .Distinct()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false })
            .ToList();

        var handlers = types
            .Select(type => (Implementation: type, Service: HandlerBase(type)))
            .Where(handler => handler.Service is not null)
            .ToList();

        var handlersByOperation = handlers.ToLookup(handler => handler.Service!.GenericTypeArguments[0]);
        var operations = types.Where(IsOperation).Concat(handlersByOperation.Select(group => group.Key)).Distinct();
        var mismatches = operations
            .Select(operation => (Operation: operation, Count: handlersByOperation[operation].Count()))
            .Where(operation => operation.Count != 1)
            .Select(operation =>
                $"{operation.Operation.FullName} has {operation.Count} handlers; exactly one is required."
            )
            .ToList();
        if (mismatches.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, mismatches));
        }

        foreach (var (implementation, service) in handlers)
        {
            AddHandler(services, service!, implementation);
        }

        foreach (var type in types)
        {
            foreach (
                var authorizer in type.GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAuthorizer<>))
            )
            {
                services.TryAddEnumerable(ServiceDescriptor.Scoped(authorizer, type));
            }
        }

        return services;
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

    private static Type? HandlerBase(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && HandlerDefinitions.Contains(current.GetGenericTypeDefinition()))
            {
                return current;
            }
        }

        return null;
    }

    private static bool IsOperation(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (
                current == typeof(Command)
                || (
                    current.IsGenericType
                    && current.GetGenericTypeDefinition() is var definition
                    && (definition == typeof(Command<>) || definition == typeof(Query<>))
                )
            )
            {
                return true;
            }
        }

        return false;
    }
}
