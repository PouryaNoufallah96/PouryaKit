using System.Text.Json.Serialization;
using Wolverine;

namespace PouryaKit.Messaging.Wolverine.Sagas;

/// <summary>
/// The base of a Saga's state and handler type: a public class with a public parameterless constructor whose steps each
/// take a <see cref="SagaInput{TSaga, TEvent}"/> or one of its <see cref="SagaTimeout"/>s, a static <c>Start</c> returning
/// the new saga, and the timeouts it schedules, or an instance <c>Handle</c>; Wolverine generates each step's chain. A step
/// changes the saga's state, emits through <see cref="PublishAsync"/> and completes the saga with
/// <see cref="MarkCompleted"/>; it returns only the saga and its timeouts, never saves, commits or sends anything itself,
/// and has no effect outside its transaction, since a step that dies before its commit runs again (ADR 0013); a step that
/// returns anything else fails, as one that emits a Snapshot does. The Kit keeps the saga's identity, the
/// tenant and correlation of the event that started it, and the identity of each timeout it scheduled.
/// </summary>
public abstract class PouryaKitSaga : Saga
{
    /// <summary>The saga's identity: the starting event's tenant with its business id, each escaped; the Kit sets it.</summary>
    public string Id { get; set; } = "";

    /// <summary>The tenant of the Operation that published the starting event, which every emission carries.</summary>
    [JsonInclude]
    public string? Tenant { get; internal set; }

    /// <summary>The correlation of the Operation that published the starting event, which every emission carries.</summary>
    [JsonInclude]
    public Guid Correlation { get; internal set; }

    // The stable identity of each timeout a step scheduled, by the timeout's envelope.
    [JsonInclude]
    internal Dictionary<Guid, Guid> Timeouts { get; set; } = [];

    /// <summary>
    /// Emits <paramref name="integrationEvent"/>, an Integration Event the saga's Module publishes, from the running step,
    /// into the Module's Outbox in the step's transaction: under the saga's declared system Actor, the starting event's
    /// tenant and correlation, the step's identity as causation and an identity every delivery of the step repeats, so an
    /// ordinary subscription receives it as any Integration Event. Throws outside a step of this saga, for a type its
    /// Module does not declare that it publishes, and for a second event of one type from one step.
    /// </summary>
    protected ValueTask PublishAsync(object integrationEvent)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return SagaStep.Running(this).EmitAsync(this, integrationEvent);
    }

    /// <summary>Completes the saga: the Kit deletes its state with the step's commit, and keeps its claims.</summary>
    protected new void MarkCompleted() => base.MarkCompleted();
}
