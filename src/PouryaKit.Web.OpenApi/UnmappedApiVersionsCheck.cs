using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;

namespace PouryaKit.Web.OpenApi;

// A Composition Root declared API versions for a route prefix no Module's endpoints are mapped under, which would otherwise
// leave that Module unversioned. A startup check: the application maps its routes after the Host's container is built.
internal sealed class UnmappedApiVersionsCheck : IStartupCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services) =>
        services.GetService<IModuleRouteGroups>() is VersionedModuleRouteGroups versioned && versioned.Unmapped().ToList() is [_, ..] unmapped
            ? [
                $"API versions are declared for the route group {string.Join(", ", unmapped)}, but no Module's endpoints are mapped under it. Declare them with the prefix the Module's AddPouryaKitEndpoints names, and map the Modules with MapPouryaKitModuleEndpoints.",
            ]
            : [];
}
