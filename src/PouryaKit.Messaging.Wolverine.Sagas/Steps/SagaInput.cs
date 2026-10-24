using Wolverine.Persistence.Sagas;

namespace PouryaKit.Messaging.Wolverine.Sagas;

// What the Kit knows of an Integration Event it delivers to a saga, whatever the event's type.
internal interface ISagaInput
{
    string SagaId { get; }

    string? Tenant { get; }

    Guid Correlation { get; }

    string WireName { get; }

    Guid EventId { get; }
}

/// <summary>
/// An Integration Event as the Kit delivers it to the Saga <typeparamref name="TSaga"/> on the saga's own queue: the
/// parameter of each of the saga's steps that <typeparamref name="TEvent"/> starts or advances. A type of its own per saga
/// and event, since the Kit's delivery handler of <typeparamref name="TEvent"/> would take the saga's place on every
/// queue, and two sagas of one event would be one chain (ADR 0013).
/// </summary>
/// <param name="SagaId">The saga's identity: the event's tenant with its business id, each escaped.</param>
/// <param name="Tenant">The tenant of the Operation that published the event.</param>
/// <param name="Correlation">The correlation of the Operation that published the event.</param>
/// <param name="WireName">The event's wire name.</param>
/// <param name="EventId">The event's identity, the same in every delivery of it.</param>
/// <param name="Event">The Integration Event.</param>
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
