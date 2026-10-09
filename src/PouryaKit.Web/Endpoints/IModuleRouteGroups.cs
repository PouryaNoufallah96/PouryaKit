using Microsoft.AspNetCore.Routing;

namespace PouryaKit.Web;

/// <summary>
/// Where <c>MapPouryaKitModuleEndpoints</c> maps a Module's endpoints inside its route group, when an optional package
/// registers one, such as <c>PouryaKit.Web.OpenApi</c> for a Module that declares API versions. Without it, each
/// Module's endpoints are mapped on its route group itself.
/// </summary>
public interface IModuleRouteGroups
{
    /// <summary>
    /// Returns the route builders inside <paramref name="moduleGroup"/>, the route group of the Module whose endpoints
    /// are mapped under <paramref name="routePrefix"/>, on which each of its endpoints is mapped; return
    /// <paramref name="moduleGroup"/> alone to keep the Module unchanged. Each builder lies inside the Module's group, so
    /// it keeps the group's authenticated default and its Operation's beginning.
    /// </summary>
    IReadOnlyList<IEndpointRouteBuilder> Map(string routePrefix, RouteGroupBuilder moduleGroup);
}
