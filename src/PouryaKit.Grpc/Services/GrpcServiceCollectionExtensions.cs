using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace PouryaKit.Grpc;

/// <summary>Declares a Module's gRPC services, which <c>MapPouryaKitGrpcServices</c> maps.</summary>
public static class GrpcServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Declares the gRPC service <typeparamref name="TService"/>, a class generated from a Protobuf contract's
        /// service base, which <c>MapPouryaKitGrpcServices</c> maps at the root, outside every Module's route group. A
        /// Module's Composition Root calls it on its builder; an application without Modules on its own services. The
        /// service is created for each call from the request's scope, where its Operation has begun, so it injects the
        /// <c>OperationContext</c> and passes it to its Module's Gateway, and answers with <c>ToGrpcResponse</c>. It also
        /// registers ASP.NET Core's gRPC services, with no endpoint for an unknown service, so a Module's HTTP endpoints
        /// keep routing's 405 and 415. Declaring a service again changes nothing.
        /// </summary>
        public IServiceCollection AddPouryaKitGrpcService<TService>()
            where TService : class
        {
            ArgumentNullException.ThrowIfNull(services);

            // No catch-all endpoint for an unknown service: it accepts every method on any two-segment path, so routing
            // would answer a Module's 405 and 415 as 404. An unknown service's call answers HTTP 404, which a gRPC client
            // reads as Unimplemented.
            services.AddGrpc(grpc => grpc.IgnoreUnknownServices = true);
            if (!services.Any(descriptor => descriptor.ImplementationInstance is GrpcService { Type: var type } && type == typeof(TService)))
            {
                services.AddSingleton(new GrpcService(typeof(TService), routes => routes.MapGrpcService<TService>()));
            }

            return services;
        }
    }
}

/// <summary>A declared gRPC service, mapped on the routes it is given.</summary>
internal sealed record GrpcService(Type Type, Action<IEndpointRouteBuilder> Map);
