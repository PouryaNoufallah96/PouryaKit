using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using PouryaKit.Cqrs;

namespace PouryaKit.Web;

// The one place the HTTP boundary establishes the caller's Actor: the request's Operation begins with this context, and
// the rate limit partitions by its Actor, so the two cannot disagree.
internal static class HttpCallerContext
{
    /// <summary>
    /// The context the request's Operation runs with. An unauthenticated caller runs as the Actor its endpoint names
    /// with <c>AllowAnonymousAs(actor)</c>. An authenticated caller runs as the person its Actor claim names, unless it
    /// is a trusted peer whose headers continue an Operation, which then runs with the continued Actor. Null when no
    /// Actor resolves: an unauthenticated caller of an endpoint that names none, or a caller without the Actor claim.
    /// Throws <see cref="KitBadRequestException"/> when a trusted peer's context headers cannot be read.
    /// </summary>
    public static OperationContext? Resolve(HttpContext httpContext, PouryaKitWebOptions options, ISet<string> peers)
    {
        var caller = httpContext.User;
        if (caller.Identity?.IsAuthenticated != true)
        {
            return httpContext.GetEndpoint()?.Metadata.GetMetadata<AnonymousActor>()?.Actor is { } anonymous
                ? new OperationContext(anonymous)
                : null;
        }

        var id = caller.FindFirstValue(options.ActorClaim);
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        // The peer's identity comes from an identity that authenticated, never from a header or an unauthenticated one.
        var trustedPeer = caller.Identities.Any(identity =>
            identity.IsAuthenticated && identity.FindFirst(options.ActorClaim)?.Value is { } peer && peers.Contains(peer)
        );
        if (trustedPeer && OperationContextHttpHeaders.Continued(httpContext.Request.Headers) is { } continued)
        {
            return continued;
        }

        // A blank tenant claim is no tenant, as the JWT bearer scheme already drops a blank claim.
        var tenant = options.TenantClaim is { } tenantClaim ? caller.FindFirstValue(tenantClaim) : null;
        return new OperationContext(Actor.Person(id), string.IsNullOrWhiteSpace(tenant) ? null : tenant);
    }
}

/// <summary>The Actor an endpoint's unauthenticated callers run as, from <c>AllowAnonymousAs(actor)</c>.</summary>
internal sealed record AnonymousActor(Actor Actor);
