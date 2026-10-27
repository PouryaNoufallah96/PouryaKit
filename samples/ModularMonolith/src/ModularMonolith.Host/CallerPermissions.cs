using System.Security.Claims;
using Microsoft.Extensions.Options;
using PouryaKit.Cqrs;
using PouryaKit.Security;
using PouryaKit.Web;

namespace ModularMonolith.Host;

#region caller-permissions
/// <summary>
/// The Host's source of permissions: the <see cref="ClaimType"/> claims the authentication scheme issued to the HTTP
/// caller whose Operation this is. Any other Actor, a named system Actor included, holds none, so independent work never
/// runs a protected command by default; a trusted continuation of an authorized Operation is not asked again.
/// </summary>
public sealed class CallerPermissions(IHttpContextAccessor http, IOptions<PouryaKitWebOptions> web) : IActorPermissions
{
    public const string ClaimType = "permission";

    public Task<bool> HasPermissionAsync(OperationContext context, string permission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var caller = http.HttpContext?.User;
        return Task.FromResult(
            caller?.Identity?.IsAuthenticated == true
                && context.Actor.Kind == ActorKind.Person
                && caller.FindFirstValue(web.Value.ActorClaim) == context.Actor.Id
                && caller.HasClaim(ClaimType, permission)
        );
    }
}
#endregion
