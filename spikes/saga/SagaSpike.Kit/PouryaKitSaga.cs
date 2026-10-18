using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Messaging;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Util;

namespace SagaSpike.Kit;

/// <summary>
/// A saga's state and handler type, Wolverine's <see cref="Saga"/> with what the Kit keeps for it: its identity, and the
/// tenant and correlation of the event that started it, which its emissions carry, a timeout's included.
/// </summary>
public abstract class PouryaKitSaga : Saga
{
    /// <summary>The saga's identity, the starting event's tenant with its business id; the Kit sets it.</summary>
    public string Id { get; set; } = "";

    /// <summary>The tenant of the Operation that published the starting event; the Kit sets it.</summary>
    public string? Tenant { get; set; }

    /// <summary>The correlation of the Operation that published the starting event; the Kit sets it.</summary>
    public Guid Correlation { get; set; }

    /// <summary>
    /// Emits <paramref name="integrationEvent"/> from this step through the Kit's adapter: written in the step's transaction,
    /// under the saga's declared system Actor, the starting event's tenant and correlation, the step's identity as causation,
    /// and an identity every delivery of the step repeats, so an ordinary subscription receives it as any Integration Event.
    /// </summary>
    protected ValueTask PublishAsync(IMessageContext context, object integrationEvent)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(integrationEvent);
        var bus = (MessageContext)context;
        var services = bus.Runtime.Services;
        if (!services.GetRequiredService<SagaSpikeOptions>().KitEmissions)
        {
            // The rejected option: what Wolverine writes for a message a saga publishes.
            return context.PublishAsync(integrationEvent);
        }

        var saga = services.GetRequiredService<SagaDeclarations>().Of(GetType());
        var (inputWireName, inputId, sagaId) = SagaStorage.InputOf(bus.Envelope!);
        // A starting step runs before the Kit stores the saga, so it reads what the Kit is about to store from its input.
        var (tenant, correlation) = Id.Length == 0 && bus.Envelope!.Message is ISagaInput starting
            ? (starting.Tenant, starting.Correlation)
            : (Tenant, Correlation);
        var step = SagaStorage.StepId(saga.Name, sagaId, inputWireName, inputId);
        DeliveryOptions emission = new();
        emission.Headers[OperationContextHeaders.ActorId] = saga.SystemActor;
        emission.Headers[OperationContextHeaders.ActorKind] = nameof(ActorKind.System);
        if (tenant is not null)
        {
            emission.Headers[OperationContextHeaders.Tenant] = tenant;
        }

        emission.Headers[OperationContextHeaders.CorrelationId] = correlation.ToString();
        emission.Headers[OperationContextHeaders.CausationId] = step.ToString();
        emission.Headers[IntegrationEventHeaders.DomainEventId] = SagaStorage.EmissionId(step, integrationEvent.GetType().ToMessageTypeName()).ToString();
        return context.PublishAsync(integrationEvent, emission);
    }
}
