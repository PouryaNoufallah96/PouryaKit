using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Realtime;

// What a Host and a Module call on the packed Realtime: the Host's publisher and listener over its connection string,
// and a Module's SSE endpoint whose rule admits an Actor's own Channel. Compiled, never run.
var services = new ServiceCollection().AddPouryaKitRealtime(connectionName: "app");
Action<IEndpointRouteBuilder> mapEvents = routes =>
    routes.MapPouryaKitRealtime("/events", (actor, channel) => channel == $"orders.{actor.Id}");
Console.WriteLine(
    $"{services.Count(descriptor => descriptor.ServiceType == typeof(IRealtimePublisher))} {mapEvents.Method.Name}"
);
