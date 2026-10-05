using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;

namespace PouryaKit.Modularity;

/// <summary>
/// The Modules added to one Host and the commands and queries each owns. It lives in the Host's service collection,
/// not in a static, so every <c>AddPouryaKitModules</c> call on the same Host shares it.
/// </summary>
internal sealed class HostModules
{
    private static readonly Type[] HandlerDefinitions =
    [
        typeof(CommandHandler<>),
        typeof(CommandHandler<,>),
        typeof(QueryHandler<,>),
    ];

    private readonly HashSet<Type> _modules = [];
    private readonly Dictionary<Type, Type> _ownerByOperation = [];

    public static HostModules In(IServiceCollection services)
    {
        if (services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(HostModules)) is { } existing)
        {
            return (HostModules)existing.ImplementationInstance!;
        }

        var modules = new HostModules();
        services.AddSingleton(modules);
        return modules;
    }

    public void Add(Type module)
    {
        if (!_modules.Add(module))
        {
            throw new InvalidOperationException(
                $"{module.FullName} is added twice. Add each Module once, in one AddPouryaKitModules call."
            );
        }
    }

    // A Composition Root registers its handlers on its ModuleBuilder, which reports each one here: the command or query
    // a handler serves belongs to that Module alone. The container resolves the last handler registered, so a second
    // Module registering or removing it would run its handler in the owner's Unit of Work, or the owner's in its own.
    public void Record(Type module, ServiceDescriptor descriptor)
    {
        if (OperationOf(descriptor) is { } operation)
        {
            EnsureNotOwnedByAnother(module, operation, "registers a handler for");
            _ownerByOperation[operation] = module;
        }
    }

    public void Release(Type module, ServiceDescriptor descriptor)
    {
        if (OperationOf(descriptor) is { } operation)
        {
            EnsureNotOwnedByAnother(module, operation, "removes the handler of");
            _ownerByOperation.Remove(operation);
        }
    }

    private void EnsureNotOwnedByAnother(Type module, Type operation, string action)
    {
        if (_ownerByOperation.TryGetValue(operation, out var owner) && owner != module)
        {
            throw new InvalidOperationException(
                $"{module.Name} {action} {operation.FullName}, which belongs to {owner.Name}. A command or query belongs to one Module; another Module asks it through its Contracts."
            );
        }
    }

    private static Type? OperationOf(ServiceDescriptor descriptor) =>
        !descriptor.IsKeyedService
        && descriptor.ServiceType is { IsConstructedGenericType: true } service
        && HandlerDefinitions.Contains(service.GetGenericTypeDefinition())
            ? service.GenericTypeArguments[0]
            : null;

    public void EnsureOwns(Type module, Type operation)
    {
        if (!_ownerByOperation.TryGetValue(operation, out var owner))
        {
            throw new InvalidOperationException(
                $"{operation.FullName} was sent through the Gateway of {module.Name}, but no Module handles it. Register its handler in its Module's Composition Root."
            );
        }

        if (owner != module)
        {
            throw new InvalidOperationException(
                $"{operation.FullName} belongs to {owner.Name}, but was sent through the Gateway of {module.Name}. Send it through IGatewayProcessor<{owner.Name}>; another Module asks {owner.Name} through its Contracts."
            );
        }
    }
}
