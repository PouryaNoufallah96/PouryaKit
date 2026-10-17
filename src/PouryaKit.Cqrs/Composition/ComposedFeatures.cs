using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PouryaKit.Cqrs;

// The Modules one Host composed and the Feature assemblies the Kit's registrations scanned for each Module and for the
// application, recorded through IModuleServices while the Host was composed. A Module counts as composed only when its
// builder records it, as AddPouryaKitModules does when it adds the Module; a declaration the Host's composition makes on
// a Module's behalf records that Module's assemblies but never composes it. It lives in the Host's service collection,
// not in a static, so every Composition Root adds to the same one and two Hosts in one process never share it; the
// Kit's container hands a copy to HostComposition when it is built.
internal sealed class ComposedFeatures
{
    public List<Type> Modules { get; } = [];

    public Dictionary<Type, List<Assembly>> ModuleAssemblies { get; } = [];

    public List<Assembly> Application { get; } = [];

    // The Modules AddPouryaKitTenancy made tenant-scoped; null is the application's own services.
    public HashSet<Type?> TenantScoped { get; } = [];

    // The assemblies AddPouryaKitCqrs scanned for each Module, and for the application as null, in the order first
    // scanned: those whose commands and queries the dispatcher runs, where another registration's add none.
    public List<(Type? Module, Assembly Assembly)> Dispatching { get; } = [];

    // The unkeyed resolver of each service the Modules keep their own of (IModuleServices.RouteByModule), by its type.
    private readonly Dictionary<Type, ServiceDescriptor> _routes = [];

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

    // Replaced, never tried: before the Host-level registration its TryAdd finds the resolver, after it the resolver
    // takes its place, and a repeat replaces the resolver with itself.
    public void Compose(IServiceCollection services, Type module)
    {
        if (!Modules.Contains(module))
        {
            Modules.Add(module);
            foreach (var route in _routes.Values)
            {
                services.Replace(route);
            }
        }
    }

    public void Route(IServiceCollection services, ServiceDescriptor route)
    {
        _routes[route.ServiceType] = route;
        if (Modules.Count > 0)
        {
            services.Replace(route);
        }
    }

    public void AddDispatching(Type? module, IEnumerable<Assembly> assemblies)
    {
        foreach (var assembly in assemblies)
        {
            if (!Dispatching.Contains((module, assembly)))
            {
                Dispatching.Add((module, assembly));
            }
        }
    }

    // A null module is the application's own services.
    public void Add(Type? module, IEnumerable<Assembly> assemblies)
    {
        var recorded = module is null ? Application : ModuleAssemblies.TryGetValue(module, out var known) ? known : ModuleAssemblies[module] = [];
        foreach (var assembly in assemblies)
        {
            if (!recorded.Contains(assembly))
            {
                recorded.Add(assembly);
            }
        }
    }
}
