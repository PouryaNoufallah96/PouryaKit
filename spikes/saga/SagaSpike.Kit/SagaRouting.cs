using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Transports.Sending;

namespace SagaSpike.Kit;

// The Kit's route to a saga: an Integration Event a saga takes is published once more, wrapped as the saga's own input
// type, to wherever that type routes, the saga's durable queue. Wolverine's own TransformedMessageRouteSource has this
// shape (JasperFx/wolverine V6.48.3, src/Wolverine/Runtime/Routing/IMessageRoute.cs:68-86); its transformations are
// internal, so the Kit implements the public IMessageRouteSource. A custom source is consulted first, and once one has
// found routes the first source that is not additive ends the search, whether or not it found any
// (src/Wolverine/Runtime/WolverineRuntime.Routing.cs:280-291): Wolverine's AgentMessages comes before ExplicitRouting, so
// an additive source here would cost the event its subscriptions' routes. This source therefore returns them itself, as
// ExplicitRouting finds them (:79-110), and ends the search. The Outbox's write and any publish of the event reach it; a
// copy fanned out from a broker does not, since SubscriptionDelivery sends those to the subscriptions' queues by name.
internal sealed class SagaRouteSource(SagaDeclarations sagas, SagaSpikeOptions options) : IMessageRouteSource
{
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
                .. inputs.SelectMany(input => runtime.RoutingFor(input.InputType).Routes.Select(inner => new SagaRoute(inner, input, options))),
            ];
    }
}

internal sealed class SagaRoute(IMessageRoute inner, SagaInputDeclaration input, SagaSpikeOptions options) : IMessageRoute
{
    public Envelope CreateForSending(object message, DeliveryOptions? delivery, ISendingAgent localDurableQueue, WolverineRuntime runtime, string? topicName) =>
        inner.CreateForSending(input.Wrap(message, delivery, options.TenantInIdentity), delivery, localDurableQueue, runtime, topicName);

    public MessageSubscriptionDescriptor Describe()
    {
        var described = inner.Describe();
        described.Description = $"{input.EventType.Name} delivered to a saga as {input.InputType.Name}";
        return described;
    }
}
