using Wolverine.Persistence.Sagas;

namespace SagaSpike.Kit;

/// <summary>What the Kit knows of an Integration Event it delivers to a saga, whatever the event's type.</summary>
public interface ISagaInput
{
    /// <summary>The saga's identity: the event's tenant with its business id.</summary>
    string SagaId { get; }

    /// <summary>The tenant of the Operation that published the event.</summary>
    string? Tenant { get; }

    /// <summary>The correlation of the Operation that published the event.</summary>
    Guid Correlation { get; }

    /// <summary>The event's wire name.</summary>
    string WireName { get; }

    /// <summary>The event's identity, its Domain Event's id, the same in every delivery of it.</summary>
    Guid EventId { get; }
}

/// <summary>
/// An Integration Event as the Kit delivers it to the saga <typeparamref name="TSaga"/>, on the saga's own queue. A type of
/// its own per saga and event: the Kit's pre-built delivery handler of <typeparamref name="TEvent"/> would shadow a saga's
/// chain of the same type on every queue, and two sagas of one event type would be one chain. Wolverine reads the saga's
/// identity from <see cref="SagaId"/>.
/// </summary>
public sealed record SagaInput<TSaga, TEvent>(
    [property: SagaIdentity] string SagaId,
    string? Tenant,
    Guid Correlation,
    string WireName,
    Guid EventId,
    TEvent Event
) : ISagaInput
    where TSaga : PouryaKitSaga
    where TEvent : class;
