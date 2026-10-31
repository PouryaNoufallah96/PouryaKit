using System.Globalization;
using Wolverine.Util;

namespace PouryaKit.Messaging.Wolverine.Sagas;

/// <summary>Declares the Integration Events that start or advance the Saga <typeparamref name="TSaga"/>, and its timeouts.</summary>
public sealed class SagaBuilder<TSaga>
    where TSaga : PouryaKitSaga
{
    private readonly SagaDeclaration _declaration;

    internal SagaBuilder(SagaDeclaration declaration) => _declaration = declaration;

    /// <summary>
    /// The Saga takes <typeparamref name="TEvent"/>, an Integration Event this Host publishes or receives, delivered to a
    /// step of the saga that takes a <see cref="SagaInput{TSaga, TEvent}"/>, a starting one or an advancing one. The saga
    /// it starts or advances is the one of the event's tenant and the business id <paramref name="key"/> returns, written
    /// as invariant text.
    /// </summary>
    public SagaBuilder<TSaga> Input<TEvent>(Func<TEvent, object> key)
        where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(key);
        // The wire name is read as the event is delivered, never here: a Composition Root runs before the Kit declares the
        // Host's wire names, and Wolverine keeps the first name it gives a type.
        _declaration.Inputs.Add(
            new SagaInputDeclaration(
                typeof(TEvent),
                typeof(SagaInput<TSaga, TEvent>),
                integrationEvent =>
                    Convert.ToString(key((TEvent)integrationEvent), CultureInfo.InvariantCulture)
                    ?? throw new InvalidOperationException(
                        $"The Saga {typeof(TSaga).FullName} found no business id in {typeof(TEvent).FullName}."
                    ),
                (integrationEvent, sagaId, tenant, correlation, eventId) =>
                    new SagaInput<TSaga, TEvent>(
                        sagaId,
                        tenant,
                        correlation,
                        typeof(TEvent).ToMessageTypeName(),
                        eventId,
                        (TEvent)integrationEvent
                    )
            )
        );
        return this;
    }

    /// <summary>The Saga schedules <typeparamref name="TTimeout"/>, which reaches the saga's own queue when it falls due.</summary>
    public SagaBuilder<TSaga> Timeout<TTimeout>()
        where TTimeout : SagaTimeout
    {
        _declaration.Timeouts.Add(typeof(TTimeout));
        return this;
    }
}
