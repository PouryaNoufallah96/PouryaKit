using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Domain;

namespace PouryaKit.Web;

public static class WebServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers what every endpoint answers with: problem responses, the handler that turns an unexpected
        /// exception into a 500 problem response without its message (add <c>app.UseExceptionHandler()</c> to the
        /// pipeline), typed IDs written as their bare values in JSON, and the claims that fill the
        /// <c>OperationContext</c>.
        /// </summary>
        public IServiceCollection AddPouryaKitWeb(Action<PouryaKitWebOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.AddProblemDetails();
            services.AddExceptionHandler<UnexpectedExceptionHandler>();
            services.ConfigureHttpJsonOptions(json =>
                json.SerializerOptions.Converters.Add(new TypedIdJsonConverterFactory())
            );
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
