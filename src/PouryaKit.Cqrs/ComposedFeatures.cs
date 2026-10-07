using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace PouryaKit.Cqrs;

// The Modules and Feature assemblies the Kit's registrations recorded while one Host was composed (IModuleServices). It
// lives in the Host's service collection, not in a static, so every Composition Root adds to the same one and two Hosts
// in one process never share it; the Kit's container hands a copy to HostComposition when it is built.
internal sealed class ComposedFeatures
{
    public OrderedDictionary<Type, List<Assembly>> Modules { get; } = [];

    public List<Assembly> Application { get; } = [];

    public static ComposedFeatures In(IServiceCollection services)
    {
        if (services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(ComposedFeatures)) is { } existing)
        {
            return (ComposedFeatures)existing.ImplementationInstance!;
        }

        var features = new ComposedFeatures();
        services.AddSingleton(features);
        return features;
    }

    // A null module is the application's own services.
    public void Add(Type? module, IEnumerable<Assembly> assemblies)
    {
        var recorded = module is null ? Application : Modules.TryGetValue(module, out var known) ? known : Modules[module] = [];
        foreach (var assembly in assemblies)
        {
            if (!recorded.Contains(assembly))
            {
                recorded.Add(assembly);
            }
        }
    }
}
