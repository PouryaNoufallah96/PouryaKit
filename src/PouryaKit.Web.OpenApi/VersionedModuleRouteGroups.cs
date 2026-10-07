using Asp.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace PouryaKit.Web.OpenApi;

// Binds the route group of each Module that declared API versions to one version set, mapped twice: under the URL
// segment the URL reader reads, and unversioned for the header reader. Both lie inside the Module's group.
internal sealed class VersionedModuleRouteGroups(
    IEnumerable<ModuleApiVersions> declarations,
    IServiceProvider services
) : IModuleRouteGroups
{
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

        var versions = moduleGroup.NewApiVersionSet(routePrefix.Trim('/'));
        declared.Declare(versions);
        var versionSet = versions.Build();
        return
        [
            moduleGroup.MapGroup("/v{version:apiVersion}").WithApiVersionSet(versionSet),
            moduleGroup.MapGroup("").WithApiVersionSet(versionSet),
        ];
    }
}
