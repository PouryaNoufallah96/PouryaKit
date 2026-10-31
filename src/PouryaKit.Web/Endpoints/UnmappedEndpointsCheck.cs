using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Core;
using PouryaKit.Cqrs;

namespace PouryaKit.Web;

// An endpoint in the Feature assemblies of a Module the Host composed, or of the application's own services, that no
// MapPouryaKitModuleEndpoints mapped: it would never answer, and every request to its route would be a 404. With API
// versions declared or not, it is a startup check: the application maps its routes after the Host's container is built.
internal sealed class UnmappedEndpointsCheck : IStartupCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        var declared = services
            .GetServices<ModuleEndpoints>()
            .SelectMany(module => module.Endpoints)
            .Select(endpoint => endpoint.GetType())
            .ToHashSet();
        var mapped = services.GetRequiredService<MappedEndpoints>();
        foreach (var module in composition.Modules.Append<Type?>(null))
        {
            var endpoints = ImplementingTypes.Of(
                typeof(IEndpoint),
                composition.FeatureAssembliesOf(module)
            );
            if (
                endpoints.Where(endpoint => !declared.Contains(endpoint)).ToList()
                is [_, ..] undeclared
            )
            {
                yield return module is null
                    ? $"The application's endpoints {Names(undeclared)} are declared by no AddPouryaKitEndpoints, so they never answer. Call AddPouryaKitEndpoints(routePrefix, assembly) on the Host's services, naming their assembly."
                    : $"The endpoints {Names(undeclared)} of {module.Name} are declared by no AddPouryaKitEndpoints, so they never answer. Call AddPouryaKitEndpoints(routePrefix) in its Composition Root.";
            }

            if (
                endpoints
                    .Where(endpoint => declared.Contains(endpoint) && !mapped.Contains(endpoint))
                    .ToList()
                is [_, ..] unmapped
            )
            {
                yield return $"The endpoints {Names(unmapped)} of {module?.Name ?? "the application"} are declared but never mapped, so they never answer. Call app.MapPouryaKitModuleEndpoints() once the Host is built.";
            }
        }
    }

    private static string Names(IEnumerable<Type> endpoints) =>
        string.Join(", ", endpoints.Select(endpoint => endpoint.FullName));
}

// The endpoints MapPouryaKitModuleEndpoints mapped in this Host, by type. It maps them while the Host is composed, before
// it starts, so nothing writes this while a check reads it.
internal sealed class MappedEndpoints
{
    private readonly HashSet<Type> _types = [];

    public void Add(Type endpoint) => _types.Add(endpoint);

    public bool Contains(Type endpoint) => _types.Contains(endpoint);
}
