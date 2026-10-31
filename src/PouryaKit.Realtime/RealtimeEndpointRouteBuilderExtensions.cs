using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Web;

namespace PouryaKit.Realtime;

/// <summary>Composes the Realtime Capability's SSE endpoint on a Module's routes.</summary>
public static class RealtimeEndpointRouteBuilderExtensions
{
    extension(IEndpointRouteBuilder routes)
    {
        /// <summary>
        /// Maps a GET at <paramref name="pattern"/> that serves server-sent events: <see cref="RealtimeEvents.Connected"/>
        /// first, then every event pushed for the request's tenant on the Channels the request names with the
        /// <c>channel</c> query parameter, each as an event whose type is its Channel and whose data is the event in the
        /// Kit's JSON, until the client disconnects or the Host stops. Map it from a Module's <c>IEndpoint</c>, whose route
        /// group requires an authenticated caller and begins the request's Operation: the tenant and the Actor are its
        /// <see cref="OperationContext"/>'s, and the connection is served by the request itself, never from inside a
        /// Gateway call: streaming opens no Gateway scope and no Unit of Work, while the request's scope and its Operation
        /// last until the connection ends. A request that names no Channel, or a blank one, is answered 400 before
        /// <paramref name="admits"/> runs, and one naming a Channel <paramref name="admits"/> refuses for the Actor 403,
        /// both as problem responses, before anything is streamed. A client a full buffer of 256 events behind is disconnected.
        /// </summary>
        public RouteHandlerBuilder MapPouryaKitRealtime(
            string pattern,
            Func<Actor, string, bool> admits
        )
        {
            ArgumentNullException.ThrowIfNull(routes);
            ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
            ArgumentNullException.ThrowIfNull(admits);

            return routes.MapGet(
                pattern,
                (
                    OperationContext context,
                    HttpRequest request,
                    RealtimeClients clients,
                    IHostApplicationLifetime lifetime
                ) =>
                {
                    string[] channels =
                    [
                        .. request
                            .Query["channel"]
                            .Distinct(StringComparer.Ordinal)
                            .OfType<string>(),
                    ];
                    Result refused = channels switch
                    {
                        [] => RealtimeErrors.ChannelRequired,
                        _ when channels.Any(string.IsNullOrWhiteSpace) =>
                            RealtimeErrors.ChannelRequired,
                        _ when !channels.All(channel => admits(context.Actor, channel)) =>
                            RealtimeErrors.ChannelNotAdmitted,
                        _ => Result.Success(),
                    };
                    return refused.IsFailure
                        ? refused.ToHttpResult()
                        : TypedResults.ServerSentEvents(
                            clients.ConnectAsync(
                                context.Tenant ?? OperationContext.NoTenant,
                                channels,
                                lifetime.ApplicationStopping
                            )
                        );
                }
            );
        }
    }
}
