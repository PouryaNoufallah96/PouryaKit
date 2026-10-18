using PouryaKit.Cqrs;
using PouryaKit.Messaging.Wolverine.Sagas;

namespace ModularMonolith.Tests.Sagas;

/// <summary>
/// A saga whose starting step takes the Kit's Operation, which only service location reaches: a test declares it to see
/// the step refused when Wolverine generates its chain. No Composition Root declares it, so no published Host carries it.
/// </summary>
public sealed class ContextProbeSaga : PouryaKitSaga
{
    /// <summary>Starts the saga, reading the Operation.</summary>
    public static ContextProbeSaga Start(SagaInput<ContextProbeSaga, OrderPlacedV1> placed, Operation operation)
    {
        ArgumentNullException.ThrowIfNull(placed);
        ArgumentNullException.ThrowIfNull(operation);
        return new();
    }
}
