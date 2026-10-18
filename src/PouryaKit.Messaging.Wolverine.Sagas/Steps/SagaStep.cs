using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using Wolverine.Runtime;

namespace PouryaKit.Messaging.Wolverine.Sagas;

// The step a generated saga chain runs: its delivery, its input and identity, and the emissions it made. The chain's own
// guard makes it current (SagaStorage.Enter), synchronously in the generated method, so it flows to the step and ends with
// the method; an async helper could not set it for its caller.
internal sealed class SagaStep(SagaDeclaration saga, MessageContext delivery, SagaStepInput? input)
{
    private static readonly AsyncLocal<SagaStep?> Current = new();

    private readonly HashSet<Type> _emitted = [];

    public SagaDeclaration Saga { get; } = saga;

    public MessageContext Delivery { get; } = delivery;

    // Null for a timeout whose saga is gone: Wolverine's chain then ends before any step.
    public SagaStepInput? Input { get; } = input;

    public void Enter() => Current.Value = this;

    public static SagaStep Running(PouryaKitSaga saga) =>
        Current.Value is { Input: not null } step && step.Saga.SagaType == saga.GetType()
            ? step
            : throw new InvalidOperationException(
                $"{saga.GetType().FullName} emitted outside one of its steps. A Saga emits only from the step Wolverine runs for it, in that step's transaction."
            );

    public ValueTask EmitAsync(PouryaKitSaga state, object integrationEvent)
    {
        var type = integrationEvent.GetType();
        var subscriptions = Delivery.Runtime.Services.GetRequiredService<Subscriptions>();
        if (!subscriptions.Publications.TryGetValue(type, out var publication) || publication.Module != Saga.Module)
        {
            throw new InvalidOperationException(
                $"The Saga {Saga.Name} emitted {type.FullName}, which {Saga.Module?.Name ?? "the application"} does not declare that it publishes. Declare it, with its wire name, in the Composition Root with Publish<{type.Name}>(wireName)."
            );
        }

        // Its identity derives from the step's and the wire name alone, as one Domain Event yields one of each type.
        if (!_emitted.Add(type))
        {
            throw new InvalidOperationException(
                $"The Saga {Saga.Name} emitted {type.Name} twice from one step. A step emits at most one Integration Event of each type: both would carry one identity, and every receiver would apply only the first."
            );
        }

        var step = Input!.Value;
        // A step that starts the saga runs before the Kit stores it, so its tenant and correlation are its input's.
        var (tenant, correlation) = state.Tenant is null && Delivery.Envelope!.Message is ISagaInput starting
            ? (starting.Tenant, starting.Correlation)
            : (state.Tenant, state.Correlation);
        // The step continues its input's trace: Wolverine writes the current activity as the parent, else the Kit writes the
        // input's own parent; the trace state is the input's, which Wolverine's spans do not carry.
        var envelope = Delivery.Envelope!;
        return OutboxWriter.WriteEmissionAsync(
            Delivery,
            integrationEvent,
            (Actor.System(Saga.SystemActor), tenant, correlation, step.StepId),
            SagaStorage.EmissionId(step.StepId, publication.WireName),
            Activity.Current?.TraceStateString ?? envelope.Headers.GetValueOrDefault(OperationContextHeaders.TraceState),
            envelope.ParentId ?? envelope.Headers.GetValueOrDefault(OperationContextHeaders.TraceParent)
        );
    }
}

// What a step's claim and identity are made of: the input's wire name and identity, a timeout's the identity its saga's
// state kept for it, and the saga's identity.
internal readonly record struct SagaStepInput(string WireName, Guid Id, string SagaId, Guid StepId);
