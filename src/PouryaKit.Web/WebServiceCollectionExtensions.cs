using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PouryaKit.Core;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Web;

/// <summary>
/// Composes the Web Capability on a Host's services, and declares endpoints on a Module's or a Host's services.
/// </summary>
public static class WebServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers what every endpoint answers with: problem responses, the handler that turns an unexpected
        /// exception into a 500 problem response without its message and a request that cannot be read into a 400
        /// problem response in every environment, status code pages that write any other failure answered with an
        /// empty body (such as routing's own 405 and 415) as a problem response, the Kit's JSON representation
        /// (<c>UsePouryaKitJson</c>, typed IDs as their bare values) for request and response bodies, the claims that
        /// fill the <c>OperationContext</c>, and authenticated access by default: the authorization fallback policy
        /// requires an authenticated caller wherever an endpoint states nothing, inside or outside a Module's route
        /// group, unless the application set a fallback policy of its own. An endpoint opts out with <c>AllowAnonymous</c>,
        /// one whose group begins the Operation, a Module's included, with <c>AllowAnonymousAs(actor)</c>. It also registers the configured
        /// defaults, each off until the Host configures it: the named CORS policies of <see cref="PouryaKitCorsOptions"/>,
        /// response compression over HTTP and HTTPS when <see cref="PouryaKitCompressionOptions"/> enables it, and the
        /// rate limit per Actor of <see cref="PouryaKitRateLimitOptions"/>, whose rejection answers 429 as a problem
        /// response. Their options are validated when the Host starts, as are the Actor and tenant claims of
        /// <see cref="PouryaKitWebOptions"/> and the peers of <see cref="PouryaKitTrustedPeerOptions"/>, however the Host
        /// configures them: at the start, or earlier, where <c>MapPouryaKitModuleEndpoints</c> or <c>BeginOperationPerRequest</c> first reads them. With the Kit's checks
        /// (<c>AddPouryaKitCompositionChecks</c>, which <c>AddPouryaKitModules</c> calls), the Host's start fails, naming
        /// the call, when an <see cref="IEndpoint"/> in the Feature assemblies of a Module it composed, or of its own
        /// services, is not declared with <c>AddPouryaKitEndpoints</c> or not mapped by
        /// <c>MapPouryaKitModuleEndpoints</c>: a startup check, since the application maps its routes after the build.
        /// </summary>
        /// <remarks>
        /// The handler sees only what runs after it, so <c>app.UseExceptionHandler()</c> comes first in the pipeline,
        /// before <c>UseRouting()</c>, <c>UseAuthentication()</c> and <c>UseAuthorization()</c>, each called
        /// explicitly: left to <c>WebApplication</c>, those three are inserted ahead of everything the application
        /// adds, and an exception thrown in authentication escapes the handler. <c>app.UseCors(name)</c> comes after
        /// <c>UseRouting()</c> and before <c>UseAuthentication()</c>, so a preflight request is answered without
        /// credentials; <c>app.UseRateLimiter()</c> comes after <c>UseAuthorization()</c>, since it partitions by the
        /// authenticated Actor: in a Host that registers authentication, an enabled rate limit throws at a request that
        /// reaches it before authentication ran, naming the order, rather than put every caller in one partition. Compression needs no call: when enabled, it
        /// is added outermost, before anything that writes a response.
        /// </remarks>
        public IServiceCollection AddPouryaKitWeb(Action<PouryaKitWebOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.AddProblemDetails();
            services.AddExceptionHandler<UnexpectedExceptionHandler>();
            // Outside Development a parameter that fails to bind answers an empty 400; throwing hands it to the
            // exception handler, which answers the same problem response in every environment.
            services.PostConfigure<RouteHandlerOptions>(routeHandlers =>
                routeHandlers.ThrowOnBadRequest = true
            );
            // Registered first, so compression wraps everything that writes a response, the status code pages included.
            ConfiguredCompression.Add(services);
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<IStartupFilter, ProblemStatusCodePages>()
            );
            ConfiguredCors.Add(services);
            ActorRateLimiting.Add(services);
            // Set once, while the Host is composed; nothing changes AuthorizationOptions after it starts.
            services.Configure<AuthorizationOptions>(authorization =>
                authorization.FallbackPolicy ??= new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .Build()
            );
            services.ConfigureHttpJsonOptions(json => json.SerializerOptions.UsePouryaKitJson());
            services.TryAddSingleton<MappedEndpoints>();
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IStartupCheck, UnmappedEndpointsCheck>()
            );
            var options = services
                .AddOptions<PouryaKitWebOptions>()
                .Validate(
                    web => !string.IsNullOrWhiteSpace(web.ActorClaim),
                    "PouryaKitWebOptions.ActorClaim names no claim to identify the Actor."
                )
                .Validate(
                    web => web.TenantClaim is null || !string.IsNullOrWhiteSpace(web.TenantClaim),
                    "PouryaKitWebOptions.TenantClaim is blank; leave it null when the application has no tenant."
                )
                .ValidateOnStart();
            if (configure is not null)
            {
                options.Configure(configure);
            }

            // The Hosts configure their peers themselves (Configure<PouryaKitTrustedPeerOptions>); validated here.
            services
                .AddOptions<PouryaKitTrustedPeerOptions>()
                .Validate(
                    trusted => !trusted.Peers.Any(string.IsNullOrWhiteSpace),
                    "PouryaKitTrustedPeerOptions.Peers lists a blank peer; name each peer by the Actor claim value of its authenticated identity."
                )
                .ValidateOnStart();
            return services;
        }

        /// <summary>
        /// Declares the endpoints of one Module: every <see cref="IEndpoint"/> in <paramref name="featureAssemblies"/>,
        /// internal types included, mapped by <c>MapPouryaKitModuleEndpoints</c> under the route group
        /// <paramref name="routePrefix"/>. A Module's Composition Root calls it on its builder, where, named with no
        /// assembly, it scans the assembly of the Module's definition (<see cref="IModuleServices"/>); named assemblies
        /// replace it. An application without Modules calls it on its own services, naming the assembly. Throws when the
        /// assemblies have no endpoint, or an endpoint has no parameterless constructor, and when the route group,
        /// compared ignoring case, as the router matches a literal, or an assembly is already declared, whose endpoints
        /// would be mapped twice.
        /// </summary>
        public IServiceCollection AddPouryaKitEndpoints(
            string routePrefix,
            params Assembly[] featureAssemblies
        )
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentException.ThrowIfNullOrWhiteSpace(routePrefix);
            ArgumentNullException.ThrowIfNull(featureAssemblies);

            List<ModuleEndpoints> declared =
            [
                .. services
                    .Select(descriptor => descriptor.ImplementationInstance)
                    .OfType<ModuleEndpoints>(),
            ];
            if (
                declared.Any(module =>
                    string.Equals(
                        module.RoutePrefix,
                        routePrefix,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
            )
            {
                throw new InvalidOperationException(
                    $"The endpoints of the route group {routePrefix} are already declared."
                );
            }

            var assemblies = IModuleServices.FeatureAssembliesOf(services, featureAssemblies);
            foreach (var module in declared)
            {
                if (
                    module
                        .Endpoints.Select(endpoint => endpoint.GetType().Assembly)
                        .FirstOrDefault(assemblies.Contains) is
                    { } assembly
                )
                {
                    throw new InvalidOperationException(
                        $"The endpoints of {assembly.GetName().Name} are already declared, for the route group {module.RoutePrefix}."
                    );
                }
            }

            List<IEndpoint> endpoints =
            [
                .. ImplementingTypes
                    .Of(typeof(IEndpoint), assemblies)
                    .Select(type => (IEndpoint)Activator.CreateInstance(type, nonPublic: true)!),
            ];
            if (endpoints.Count == 0)
            {
                throw new InvalidOperationException(
                    assemblies is []
                        ? $"AddPouryaKitEndpoints names no assembly for the route group {routePrefix}, and on the Host's own services it infers none. Name the assembly of the endpoints."
                        : $"{string.Join(", ", assemblies.Select(assembly => assembly.GetName().Name))} has no IEndpoint for the route group {routePrefix}."
                );
            }

            services.AddSingleton(new ModuleEndpoints(routePrefix, endpoints));
            return services;
        }
    }
}

/// <summary>The endpoints one Module declared and the route group they are mapped under.</summary>
internal sealed record ModuleEndpoints(string RoutePrefix, IReadOnlyList<IEndpoint> Endpoints);
