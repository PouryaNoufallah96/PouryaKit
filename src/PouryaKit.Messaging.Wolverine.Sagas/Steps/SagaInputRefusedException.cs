namespace PouryaKit.Messaging.Wolverine.Sagas;

/// <summary>
/// Thrown by the Kit before any step of a Saga, for an Integration Event that names no saga it may change: one with no
/// tenant, whose saga identity would hold none, or one whose identity, its tenant with its business id, names a saga that
/// does not exist and that the event does not start, such as another tenant's. The delivery is retried, then set aside
/// as a dead letter; nothing is claimed, changed or emitted.
/// </summary>
public sealed class SagaInputRefusedException(string saga, string message) : Exception(message)
{
    /// <summary>The name of the Saga the delivery arrived for.</summary>
    public string Saga { get; } = saga;
}
