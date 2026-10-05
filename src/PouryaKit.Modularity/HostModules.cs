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
    // a handler serves belongs to that Module.
    public void Record(Type module, ServiceDescriptor descriptor)
    {
        if (
            !descriptor.IsKeyedService
            && descriptor.ServiceType is { IsConstructedGenericType: true } service
            && HandlerDefinitions.Contains(service.GetGenericTypeDefinition())
        )
        {
            _ownerByOperation.TryAdd(service.GenericTypeArguments[0], module);
        }
    }

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
