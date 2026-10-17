using System.Globalization;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Web;

namespace PouryaKit.Grpc;

/// <summary>Maps the gRPC services every Module declared on a Host's routes.</summary>
public static class GrpcEndpointRouteBuilderExtensions
{
    extension(IEndpointRouteBuilder routes)
    {
        /// <summary>
        /// Maps every service declared with <c>AddPouryaKitGrpcService</c> on one route group of its own at the root, never
        /// inside a Module's prefixed or versioned route group, since a gRPC method's path is its Protobuf package,
        /// service and method. The group requires an authenticated caller and begins each call's Operation as a Module's
        /// HTTP endpoints do (<c>BeginOperationPerRequest</c>): the Actor is the authenticated person, and only a trusted
        /// peer's Kit headers, in call metadata, continue an Operation of its own. A peer's headers the Kit refuses as
        /// unreadable answer the status <see cref="StatusCode.InvalidArgument"/> with the rule they broke as its message,
        /// before the service runs, where an HTTP endpoint answers a 400 problem response.
        /// </summary>
        public IEndpointRouteBuilder MapPouryaKitGrpcServices()
        {
            ArgumentNullException.ThrowIfNull(routes);

            var group = routes.MapGroup("").RequireAuthorization().BeginOperationPerRequest();
            ((IEndpointConventionBuilder)group).Finally(AnswerRefusalsAsStatus);
            var mapped = routes.ServiceProvider.GetService<MappedGrpcServices>();
            foreach (var service in routes.ServiceProvider.GetServices<GrpcService>())
            {
                service.Map(group);
                mapped?.Add(service.Type);
            }

            return routes;
        }
    }

    // Wraps the step that begins the Operation, which reads a trusted peer's headers before the gRPC delegate runs and
    // raises a refusal as Web's KitBadRequestException, which would otherwise answer an HTTP 400 that a gRPC client reads
    // as a failed call with no reason. The refusal is a trailers-only response: status and message in the headers, no
    // message body.
    private static void AnswerRefusalsAsStatus(EndpointBuilder endpoint)
    {
        var run =
            endpoint.RequestDelegate
            ?? throw new InvalidOperationException($"{endpoint.DisplayName} has no request delegate to run.");
        endpoint.RequestDelegate = async httpContext =>
        {
            try
            {
                await run(httpContext);
            }
            catch (KitBadRequestException refusal) when (!httpContext.Response.HasStarted)
            {
                httpContext.Response.StatusCode = StatusCodes.Status200OK;
                httpContext.Response.ContentType = "application/grpc";
                httpContext.Response.Headers["grpc-status"] = ((int)StatusCode.InvalidArgument).ToString(CultureInfo.InvariantCulture);
                httpContext.Response.Headers["grpc-message"] = Uri.EscapeDataString(refusal.Message);
            }
        };
    }
}
