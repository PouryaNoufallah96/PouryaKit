using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PouryaKit.Cqrs;

namespace PouryaKit.Web;

/// <summary>
/// Composes the Web Capability's endpoints on a Host's routes: maps every Module's endpoints, and states an endpoint's
/// public Actor or its <c>Idempotency-Key</c>.
/// </summary>
public static class WebEndpointRouteBuilderExtensions
{
    extension(IEndpointRouteBuilder routes)
    {
        /// <summary>
        /// Maps every Module's endpoints under its own route group, which requires an authenticated caller and begins
        /// each request's Operation before the endpoint binds (<see cref="BeginOperationPerRequest"/>). The endpoint
        /// injects that <see cref="OperationContext"/> and passes it to its Module's Gateway. Called again, it maps no
        /// endpoint twice, since two identical endpoints would make every request to its route ambiguous.
        /// </summary>
        public IEndpointRouteBuilder MapPouryaKitModuleEndpoints()
        {
            ArgumentNullException.ThrowIfNull(routes);

            var options = routes.ServiceProvider.GetRequiredService<IOptions<PouryaKitWebOptions>>().Value;
            var peers = routes.ServiceProvider.GetRequiredService<IOptions<PouryaKitTrustedPeerOptions>>().Value.Peers;
            var moduleRouteGroups = routes.ServiceProvider.GetService<IModuleRouteGroups>();
            var mapped = routes.ServiceProvider.GetService<MappedEndpoints>();
            foreach (var module in routes.ServiceProvider.GetServices<ModuleEndpoints>())
            {
                // Recorded once every route builder of the Module has its endpoints, a versioned Module's two included.
                if (module.Endpoints.Where(endpoint => mapped?.Contains(endpoint.GetType()) is not true).ToList() is not [_, ..] endpoints)
                {
                    continue;
                }

                var group = BeginOperationOn(routes.MapGroup(module.RoutePrefix).RequireAuthorization(), options, peers);
                foreach (var target in moduleRouteGroups?.Map(module.RoutePrefix, group) ?? [group])
                {
                    foreach (var endpoint in endpoints)
                    {
                        endpoint.Map(target);
                    }
                }

                endpoints.ForEach(endpoint => mapped?.Add(endpoint.GetType()));
            }

            return routes;
        }
    }

    extension(RouteGroupBuilder group)
    {
        /// <summary>
        /// Begins each request's Operation on every endpoint of this route group before the endpoint binds its
        /// parameters, so an endpoint outside a Module's route group injects the <see cref="OperationContext"/> as a
        /// Module's endpoint does. The context is built once from the caller: the Actor is the person the
        /// <see cref="PouryaKitWebOptions.ActorClaim"/> names, and the Operation starts a new chain of work. Only a caller
        /// authenticated as a peer in <see cref="PouryaKitTrustedPeerOptions.Peers"/> continues an Operation of its own
        /// instead, from the Kit's headers; every other caller's headers are ignored. An endpoint that requires an
        /// <c>Idempotency-Key</c> (<c>RequireIdempotencyKey</c>) adds the header's value as the context's request id. It
        /// requires no authenticated caller itself: the group states that with <c>RequireAuthorization</c>, as a
        /// Module's group does. It applies once per endpoint: called again on a nested group, a Module's included, it
        /// adds nothing.
        /// </summary>
        public RouteGroupBuilder BeginOperationPerRequest()
        {
            ArgumentNullException.ThrowIfNull(group);

            var services = ((IEndpointRouteBuilder)group).ServiceProvider;
            return BeginOperationOn(
                group,
                services.GetRequiredService<IOptions<PouryaKitWebOptions>>().Value,
                services.GetRequiredService<IOptions<PouryaKitTrustedPeerOptions>>().Value.Peers
            );
        }
    }

    extension<TBuilder>(TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        /// <summary>
        /// States that the endpoint is public: it answers a caller who is not authenticated, and that caller's
        /// Operation runs as <paramref name="actor"/>, which the Feature names, such as
        /// <c>Actor.System("shop.visitor")</c>. An authenticated caller still runs as themselves. Plain
        /// <c>AllowAnonymous</c> on an endpoint whose group begins the Operation (<see cref="BeginOperationPerRequest"/>),
        /// a Module's included, fails when the endpoint is built: no Operation runs as an unnamed Actor.
        /// </summary>
        public TBuilder AllowAnonymousAs(Actor actor)
        {
            ArgumentNullException.ThrowIfNull(actor);
            return builder.AllowAnonymous().WithMetadata(new AnonymousActor(actor));
        }

        /// <summary>
        /// Requires an <c>Idempotency-Key</c> header on every request to a POST, PUT, PATCH or DELETE endpoint whose command
        /// opts in with <see cref="IdempotentAttribute"/>. The header's value becomes the request's
        /// <see cref="OperationContext.RequestId"/>, so a repeat by the same Actor and tenant answers what the first
        /// request answered, through the same Result and the same <c>ToHttpResult</c> mapping, without running the
        /// command again. A request without a single header of 1 to 256 visible ASCII characters, with spaces only
        /// between them, is answered 400 as a problem response whose detail states that rule, before its body is read.
        /// The requirement is the endpoint's <see cref="IdempotencyKeyRequirement"/> metadata, which the endpoint's OpenAPI
        /// operation states. On an endpoint whose group begins the Operation, a Module's included, mapped for GET, HEAD,
        /// OPTIONS or TRACE, whose query ignores the key, or mapped for no method, which answers every method, GET
        /// included, it throws when the endpoint is built, which fails the Host's start; a command that does not opt in is
        /// refused when it is dispatched, since only then is it known.
        /// </summary>
        public TBuilder RequireIdempotencyKey() => builder.WithMetadata(new IdempotencyKeyRequirement());
    }

    private static RouteGroupBuilder BeginOperationOn(RouteGroupBuilder group, PouryaKitWebOptions options, ISet<string> peers)
    {
        ((IEndpointConventionBuilder)group).Finally(endpoint => BeginOperationFirst(endpoint, options, peers));
        return group;
    }

    // Runs as the endpoint's own first step, after authorization and before the endpoint binds its parameters, which
    // is where it resolves the OperationContext; an endpoint filter would run after that binding. The request id is part
    // of that context, so the Idempotency-Key header is read here too.
    private static void BeginOperationFirst(EndpointBuilder endpoint, PouryaKitWebOptions options, ISet<string> peers)
    {
        // A nested group that also begins the Operation runs this again on the same endpoint, which one wrapper serves.
        if (endpoint.Metadata.OfType<OperationBegunPerRequest>().Any())
        {
            return;
        }

        endpoint.Metadata.Add(new OperationBegunPerRequest());
        if (!endpoint.Metadata.OfType<AnonymousActor>().Any() && endpoint.Metadata.OfType<IAllowAnonymous>().Any())
        {
            throw new InvalidOperationException(
                $"{endpoint.DisplayName} allows anonymous callers but names no Actor for them. State it with AllowAnonymousAs(actor)."
            );
        }

        var run =
            endpoint.RequestDelegate
            ?? throw new InvalidOperationException($"{endpoint.DisplayName} has no request delegate to run.");
        var requiresIdempotencyKey = endpoint.Metadata.OfType<IdempotencyKeyRequirement>().Any();
        // A safe method sends a query, which ignores the request id, so the key would be required and change nothing; an
        // endpoint mapped for no method, without method metadata or with an empty list, answers every method, GET
        // included. Whether the endpoint's command opts in with [Idempotent] is known only when it is dispatched (ADR 0002).
        if (requiresIdempotencyKey)
        {
            var methods = endpoint.Metadata.OfType<IHttpMethodMetadata>().LastOrDefault()?.HttpMethods ?? [];
            if (methods.Count == 0)
            {
                throw new InvalidOperationException(
                    $"{endpoint.DisplayName} requires an Idempotency-Key but is mapped for no HTTP method, so it answers every method, GET included, and a query ignores the key. Map the command on POST, PUT, PATCH or DELETE, or remove RequireIdempotencyKey()."
                );
            }

            if (methods.FirstOrDefault(IsSafe) is { } safe)
            {
                throw new InvalidOperationException(
                    $"{endpoint.DisplayName} requires an Idempotency-Key on {safe}, a method that only reads, and a query ignores the key. Remove RequireIdempotencyKey(), or map the command on POST, PUT, PATCH or DELETE."
                );
            }
        }
        endpoint.RequestDelegate = httpContext =>
        {
            var context = HttpCallerContext.Resolve(httpContext, options, peers) ?? throw Unresolved(httpContext, options);
            httpContext.RequestServices.BeginOperation(
                requiresIdempotencyKey
                    ? context with { RequestId = IdempotencyKeyHeader.Read(httpContext.Request.Headers) }
                    : context
            );
            return run(httpContext);
        };
    }

    private sealed class OperationBegunPerRequest;

    private static bool IsSafe(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);

    private static InvalidOperationException Unresolved(HttpContext httpContext, PouryaKitWebOptions options) =>
        httpContext.User.Identity?.IsAuthenticated == true
            ? new InvalidOperationException(
                $"The authenticated caller has no {options.ActorClaim} claim to identify the Actor. Set PouryaKitWebOptions.ActorClaim to the claim the authentication scheme issues."
            )
            : new InvalidOperationException(
                "An unauthenticated request reached an endpoint that is not public. Add authorization to the pipeline."
            );
}
