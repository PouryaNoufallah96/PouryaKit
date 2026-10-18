using PouryaKit.Cqrs;
using PouryaKit.Messaging;
using SagaSpike.Contracts;
using SagaSpike.Kit;
using Wolverine;

namespace SagaSpike.Shipping;

/// <summary>
/// The rejected route: a plain Wolverine saga started by the Integration Event itself, routed to its own queue as Wolverine
/// routes any message. A test Host includes it; no Composition Root declares it.
/// </summary>
public sealed class NaiveSaga : Saga
{
    /// <summary>The order, as Wolverine reads the identity from the message's own member.</summary>
    public string Id { get; set; } = "";

    /// <summary>Starts the saga.</summary>
    public static NaiveSaga Start(OrderPlacedV1 placed) => new() { Id = placed.OrderId.ToString() };
}

/// <summary>
/// Started beside the fulfilment saga by the same event, for the middleware test: what a step sees of the Operation, and
/// whether the Kit's Integration Event publisher serves it.
/// </summary>
public sealed class ContextProbeSaga : PouryaKitSaga
{
    /// <summary>Starts the saga, recording what the step saw.</summary>
    public static ContextProbeSaga Start(SagaInput<ContextProbeSaga, OrderPlacedV1> placed, Operation operation, IIntegrationEventPublisher events)
    {
        string? refused = null;
        try
        {
            events.Publish(placed.EventId, new OrderFulfilledV1(placed.Event.OrderId, 0));
        }
        catch (InvalidOperationException exception)
        {
            refused = exception.Message;
        }

        ContextProbe.Seen[placed.Event.OrderId] = new SeenStep(operation.Context, operation.Module, operation.IsUnitOfWorkActive, refused);
        return new ContextProbeSaga();
    }
}

/// <summary>What the probe saga's step saw.</summary>
public sealed record SeenStep(OperationContext? Context, Type? Module, bool UnitOfWorkActive, string? PublishRefused);

/// <summary>What each probe saga's step saw, by order.</summary>
public static class ContextProbe
{
    /// <summary>The steps seen, by order.</summary>
    public static System.Collections.Concurrent.ConcurrentDictionary<Guid, SeenStep> Seen { get; } = [];
}

/// <summary>
/// The tried middleware: before the step, it begins an Operation on the step's service scope with the Operation Context
/// the event's headers carry, as the Kit's delivery step does, but for the declared system Actor.
/// </summary>
public static class RestoreOperationContext
{
    /// <summary>Begins the Operation of the step's scope.</summary>
    public static void Before(Envelope envelope, IServiceProvider services)
    {
        string Header(string name) => envelope.Headers[name]!;

        services.BeginOperation(
            new OperationContext(
                Actor.System("shipping.context-probe"),
                envelope.Headers.GetValueOrDefault(OperationContextHeaders.Tenant),
                Guid.Parse(Header(OperationContextHeaders.CorrelationId)),
                Guid.Parse(Header(OperationContextHeaders.CausationId))
            ),
            typeof(ShippingModule)
        );
    }
}
