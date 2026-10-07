using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PouryaKit.Cqrs;

namespace PouryaKit.Web;

public static class WebEndpointRouteBuilderExtensions
{
    extension(IEndpointRouteBuilder routes)
    {
        /// <summary>
        /// Maps every Module's endpoints under its own route group, which requires an authenticated caller. Before an
        /// endpoint runs, the request's Operation is begun with an <see cref="OperationContext"/> built once from the
        /// caller: the Actor is the person the <see cref="PouryaKitWebOptions.ActorClaim"/> names, and the Operation
        /// starts a new chain of work. Only a caller authenticated as a peer in
        /// <see cref="PouryaKitTrustedPeerOptions.Peers"/> continues an Operation of its own instead, from the Kit's
        /// headers; every other caller's headers are ignored. The endpoint injects that <see cref="OperationContext"/>
        /// and passes it to its Module's Gateway.
        /// </summary>
        public IEndpointRouteBuilder MapPouryaKitModuleEndpoints()
        {
            ArgumentNullException.ThrowIfNull(routes);

            var options = routes.ServiceProvider.GetRequiredService<IOptions<PouryaKitWebOptions>>().Value;
            var peers = routes.ServiceProvider.GetRequiredService<IOptions<PouryaKitTrustedPeerOptions>>().Value.Peers;
            var moduleRouteGroups = routes.ServiceProvider.GetService<IModuleRouteGroups>();
            foreach (var module in routes.ServiceProvider.GetServices<ModuleEndpoints>())
            {
                var group = routes.MapGroup(module.RoutePrefix).RequireAuthorization();
                ((IEndpointConventionBuilder)group).Finally(endpoint => BeginOperationFirst(endpoint, options, peers));
                foreach (var target in moduleRouteGroups?.Map(module.RoutePrefix, group) ?? [group])
                {
                    foreach (var endpoint in module.Endpoints)
                    {
                        endpoint.Map(target);
                    }
                }
            }

            return routes;
        }
    }

    extension<TBuilder>(TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        /// <summary>
        /// States that the endpoint is public: it answers a caller who is not authenticated, and that caller's
        /// Operation runs as <paramref name="actor"/>, which the Feature names, such as
        /// <c>Actor.System("catalog.storefront")</c>. An authenticated caller still runs as themselves. Plain
        /// <c>AllowAnonymous</c> on a Module's endpoint fails when the endpoint is built: no Operation runs as an
        /// unnamed Actor.
        /// </summary>
        public TBuilder AllowAnonymousAs(Actor actor)
        {
            ArgumentNullException.ThrowIfNull(actor);
            return builder.AllowAnonymous().WithMetadata(new AnonymousActor(actor));
        }
    }

    // Runs as the endpoint's own first step, after authorization and before the endpoint binds its parameters, which
    // is where it resolves the OperationContext; an endpoint filter would run after that binding.
    private static void BeginOperationFirst(EndpointBuilder endpoint, PouryaKitWebOptions options, ISet<string> peers)
    {
        var anonymousActor = endpoint.Metadata.OfType<AnonymousActor>().LastOrDefault()?.Actor;
        if (anonymousActor is null && endpoint.Metadata.OfType<IAllowAnonymous>().Any())
        {
            throw new InvalidOperationException(
                $"{endpoint.DisplayName} allows anonymous callers but names no Actor for them. State it with AllowAnonymousAs(actor)."
            );
        }

        var run =
            endpoint.RequestDelegate
            ?? throw new InvalidOperationException($"{endpoint.DisplayName} has no request delegate to run.");
        endpoint.RequestDelegate = httpContext =>
        {
            httpContext.RequestServices.BeginOperation(CallerContext(httpContext, anonymousActor, options, peers));
            return run(httpContext);
        };
    }

    private static OperationContext CallerContext(
        HttpContext httpContext,
        Actor? anonymousActor,
        PouryaKitWebOptions options,
        ISet<string> peers
    )
    {
        var caller = httpContext.User;
        if (caller.Identity?.IsAuthenticated != true)
        {
            return new OperationContext(
                anonymousActor
                    ?? throw new InvalidOperationException(
                        "An unauthenticated request reached an endpoint that is not public. Add authorization to the pipeline."
                    )
            );
        }

        var id = caller.FindFirstValue(options.ActorClaim);
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException(
                $"The authenticated caller has no {options.ActorClaim} claim to identify the Actor. Set PouryaKitWebOptions.ActorClaim to the claim the authentication scheme issues."
            );
        }

        // The peer's identity comes from an identity that authenticated, never from a header or an unauthenticated one.
        var trustedPeer = caller.Identities.Any(identity =>
            identity.IsAuthenticated && identity.FindFirst(options.ActorClaim)?.Value is { } peer && peers.Contains(peer)
        );
        if (trustedPeer && OperationContextHttpHeaders.Continued(httpContext.Request.Headers) is { } continued)
        {
            return continued;
        }

        return new OperationContext(
            Actor.Person(id),
            options.TenantClaim is { } tenantClaim ? caller.FindFirstValue(tenantClaim) : null
        );
    }

    private sealed record AnonymousActor(Actor Actor);
}
