using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PouryaKit.Domain;

namespace PouryaKit.Web;

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
        /// group, unless the application set a fallback policy of its own. An endpoint opts out with
        /// <c>AllowAnonymous</c>, on a Module's endpoint <c>AllowAnonymousAs(actor)</c>.
        /// </summary>
        /// <remarks>
        /// The handler sees only what runs after it, so <c>app.UseExceptionHandler()</c> comes first in the pipeline,
        /// before <c>UseRouting()</c>, <c>UseAuthentication()</c> and <c>UseAuthorization()</c>, each called
        /// explicitly: left to <c>WebApplication</c>, those three are inserted ahead of everything the application
        /// adds, and an exception thrown in authentication escapes the handler.
        /// </remarks>
        public IServiceCollection AddPouryaKitWeb(Action<PouryaKitWebOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.AddProblemDetails();
            services.AddExceptionHandler<UnexpectedExceptionHandler>();
            // Outside Development a parameter that fails to bind answers an empty 400; throwing hands it to the
            // exception handler, which answers the same problem response in every environment.
            services.PostConfigure<RouteHandlerOptions>(routeHandlers => routeHandlers.ThrowOnBadRequest = true);
            services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, ProblemStatusCodePages>());
            // Set once, while the Host is composed; nothing changes AuthorizationOptions after it starts.
            services.Configure<AuthorizationOptions>(authorization =>
                authorization.FallbackPolicy ??= new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()
            );
            services.ConfigureHttpJsonOptions(json => json.SerializerOptions.UsePouryaKitJson());
            var options = services.AddOptions<PouryaKitWebOptions>();
            if (configure is not null)
            {
                options.Configure(configure);
            }

            return services;
        }

        /// <summary>
        /// Declares the endpoints of one Module: every <see cref="IEndpoint"/> in <paramref name="endpointAssembly"/>,
        /// internal types included, mapped by <c>MapPouryaKitModuleEndpoints</c> under the route group
        /// <paramref name="routePrefix"/>. A Module's Composition Root calls it on its builder; an application without
        /// Modules calls it on its own services. Throws when the assembly has no endpoint, or an endpoint has no
        /// parameterless constructor.
        /// </summary>
        public IServiceCollection AddPouryaKitEndpoints(string routePrefix, Assembly endpointAssembly)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentException.ThrowIfNullOrWhiteSpace(routePrefix);
            ArgumentNullException.ThrowIfNull(endpointAssembly);

            List<IEndpoint> endpoints =
            [
                .. endpointAssembly
                    .GetTypes()
                    .Where(type =>
                        type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
                        && type.IsAssignableTo(typeof(IEndpoint))
                    )
                    .Select(type => (IEndpoint)Activator.CreateInstance(type, nonPublic: true)!),
            ];
            if (endpoints.Count == 0)
            {
                throw new InvalidOperationException(
                    $"{endpointAssembly.GetName().Name} has no IEndpoint for the route group {routePrefix}."
                );
            }

            services.AddSingleton(new ModuleEndpoints(routePrefix, endpoints));
            return services;
        }
    }
}

/// <summary>The endpoints one Module declared and the route group they are mapped under.</summary>
internal sealed record ModuleEndpoints(string RoutePrefix, IReadOnlyList<IEndpoint> Endpoints);
