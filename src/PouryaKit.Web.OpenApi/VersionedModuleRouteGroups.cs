using Asp.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;

namespace PouryaKit.Web.OpenApi;

// Binds the route group of each Module that declared API versions to one version set, mapped twice: under the URL
// segment the URL reader reads, and unversioned for the header reader. Both lie inside the Module's group.
internal sealed class VersionedModuleRouteGroups(
    IEnumerable<ModuleApiVersions> declarations,
    IServiceProvider services
) : IModuleRouteGroups
{
    private readonly HashSet<string> _mapped = [];

    public IReadOnlyList<IEndpointRouteBuilder> Map(string routePrefix, RouteGroupBuilder moduleGroup)
    {
        var declared = declarations.SingleOrDefault(declaration => declaration.RoutePrefix == routePrefix);
        if (declared is null)
        {
            return [moduleGroup];
        }

        if (services.GetService<IApiVersionParameterSource>() is null)
        {
            throw new InvalidOperationException(
                $"The Module mapped under {routePrefix} declares API versions, but the Host does not register them. Call AddPouryaKitOpenApi on the Host's services."
            );
        }

        _mapped.Add(routePrefix);
        var versions = moduleGroup.NewApiVersionSet(routePrefix.Trim('/'));
        declared.Declare(versions);
        var versionSet = versions.Build();
        var unversioned = moduleGroup.MapGroup("").WithApiVersionSet(versionSet);
        // An endpoint name is unique in the Host, so it belongs to the versioned path; its unversioned copy has none.
        ((IEndpointConventionBuilder)unversioned).Finally(endpoint =>
        {
            foreach (var name in endpoint.Metadata.Where(metadata => metadata is IEndpointNameMetadata or IRouteNameMetadata).ToList())
            {
                endpoint.Metadata.Remove(name);
            }
        });
        return [moduleGroup.MapGroup("/v{version:apiVersion}").WithApiVersionSet(versionSet), unversioned];
    }

    /// <summary>The declared route prefixes no Module's endpoints were mapped under.</summary>
    public IEnumerable<string> Unmapped() =>
        declarations.Select(declaration => declaration.RoutePrefix).Where(prefix => !_mapped.Contains(prefix));
}

// A Composition Root declared API versions for a route prefix no Module's endpoints are mapped under, which would otherwise
// leave that Module unversioned. It runs when the Host starts, after the application mapped its routes.
internal sealed class UnmappedApiVersionsCheck : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services) =>
        services.GetService<IModuleRouteGroups>() is VersionedModuleRouteGroups versioned && versioned.Unmapped().ToList() is [_, ..] unmapped
            ? [
                $"API versions are declared for the route group {string.Join(", ", unmapped)}, but no Module's endpoints are mapped under it. Declare them with the prefix the Module's AddPouryaKitEndpoints names, and map the Modules with MapPouryaKitModuleEndpoints.",
            ]
            : [];
}
