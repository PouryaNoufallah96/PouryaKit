using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Transports.Sending;

namespace PouryaKit.Messaging.Wolverine.Sagas;

// The Kit's route to a saga: an Integration Event a saga takes is published once more, as the saga's input type, to
// wherever that type routes, the saga's durable queue, beside the event's own routes. Wolverine consults a custom source
// first, and once one has found routes the first source that is not additive ends the search, whether or not it found
// any (JasperFx/wolverine V6.52.0, src/Wolverine/Runtime/WolverineRuntime.Routing.cs:315-335), so an additive source here
// would cost the event its subscriptions' routes: this one returns them itself, as ExplicitRouting finds them, and ends
// the search. A copy fanned out from a broker does not come this way: SubscriptionDelivery sends it by queue.
internal sealed class SagaRouteSource(SagaDeclarations sagas) : IMessageRouteSource
{
    private static readonly Dictionary<string, string?> NoHeaders = [];

    public bool IsAdditive => false;

    public IEnumerable<IMessageRoute> FindRoutes(Type messageType, IWolverineRuntime runtime)
    {
        var inputs = sagas.All.SelectMany(saga => saga.Inputs.Where(input => input.EventType == messageType)).ToArray();
        return inputs.Length == 0
            ? []
            :
            [
                .. runtime
                    .Options.Transports.AllEndpoints()
                    .Where(endpoint => endpoint.Subscriptions.Any(subscription => subscription.Matches(messageType)))
                    .Select(endpoint => MessageRoute.For(messageType, endpoint, runtime)),
                .. inputs.SelectMany(input => runtime.RoutingFor(input.InputType).Routes.Select(inner => new SagaRoute(inner, input))),
            ];
    }

    private sealed class SagaRoute(IMessageRoute inner, SagaInputDeclaration input) : IMessageRoute
    {
        public Envelope CreateForSending(object message, DeliveryOptions? options, ISendingAgent localDurableQueue, WolverineRuntime runtime, string? topicName) =>
            inner.CreateForSending(input.Wrap(message, options?.Headers ?? NoHeaders), options, localDurableQueue, runtime, topicName);

        public MessageSubscriptionDescriptor Describe()
        {
            var described = inner.Describe();
            described.Description = $"{input.EventType.Name} delivered to a Saga as {input.InputType.Name}";
            return described;
        }
    }
}
