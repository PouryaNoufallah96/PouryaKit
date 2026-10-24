using System.Globalization;
using Npgsql;
using Wolverine;
using Wolverine.Persistence.Sagas;
using Wolverine.RDBMS.Sagas;
using Wolverine.Util;

namespace PouryaKit.Messaging.Wolverine.Sagas;

internal interface ISagaStepStorage
{
    bool AlreadyApplied { get; }

    void Enter(bool startsSaga);
}

// Wolverine's storage on the Kit's transaction. A saga inserted takes its identity, tenant and correlation from the event
// that started it, so its own Start never sets them, and a saga stored keeps the identity of each timeout the step
// scheduled. An advancing input whose identity names no saga is the Kit's refusal, where Wolverine would throw its own. A
// completed saga's state, in the qualified state table, is deleted only at the version the step loaded.
internal sealed class SagaStepStorage<TId, TSaga>(
    DatabaseSagaStorage<TId, TSaga> inner,
    NpgsqlTransaction transaction,
    string table,
    SagaStep step,
    bool alreadyApplied
) : ISagaStorage<TId, TSaga>,
        ISagaStepStorage
    where TSaga : Saga
{
    private bool _startsSaga;

    public bool AlreadyApplied { get; } = alreadyApplied;

    public void Enter(bool startsSaga)
    {
        _startsSaga = startsSaga;
        step.Enter();
    }

    public async Task<TSaga?> LoadAsync(TId id, CancellationToken cancellationToken)
    {
        var saga = await inner.LoadAsync(id, cancellationToken);
        return saga is not null || _startsSaga || step.Delivery.Envelope!.Message is not ISagaInput input
            ? saga
            : throw new SagaInputRefusedException(
                step.Saga.Name,
                $"The Integration Event {input.WireName} {input.EventId} names the saga {input.SagaId} of {step.Saga.Name}, which does not exist and which it does not start: no saga of its tenant with its business id was started, or that saga completed."
            );
    }

    public Task InsertAsync(TSaga state, CancellationToken cancellationToken)
    {
        if (state is PouryaKitSaga kit && step.Delivery.Envelope!.Message is ISagaInput starting)
        {
            kit.Id = starting.SagaId;
            kit.Tenant = starting.Tenant;
            kit.Correlation = starting.Correlation;
            KeepTimeouts(kit);
        }

        return inner.InsertAsync(state, cancellationToken);
    }

    public Task UpdateAsync(TSaga state, CancellationToken cancellationToken)
    {
        if (state is PouryaKitSaga kit)
        {
            KeepTimeouts(kit);
        }

        return inner.UpdateAsync(state, cancellationToken);
    }

    // Wolverine deletes by identity alone, so two steps that loaded one state would both complete it and both commit what
    // they emitted. The Kit deletes at the loaded version, as Wolverine updates, and otherwise throws Wolverine's
    // exception of an update: the step rolls back, and its retry runs on the state another step committed.
    public async Task DeleteAsync(TSaga state, CancellationToken cancellationToken)
    {
        var saga = (PouryaKitSaga)(Saga)state;
        await using var delete = new NpgsqlCommand(
            string.Format(CultureInfo.InvariantCulture, SagaStepStatements.Complete(table), "$1", "$2"),
            transaction.Connection,
            transaction
        )
        {
            Parameters = { new() { Value = saga.Id }, new() { Value = saga.Version } },
        };
        if (await delete.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new SagaConcurrencyException(
                $"The Saga {step.Saga.Name} {saga.Id} cannot complete at version {saga.Version}, the one its step loaded: another step changed or completed it since."
            );
        }
    }

    // Before the commit: what the step returned and Wolverine cascaded, beside its timeouts, would be sent without the
    // Kit's headers, so the step fails and its transaction rolls back.
    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (step.Delivery.Outstanding.FirstOrDefault(envelope => envelope.Message is not TimeoutMessage && !envelope.Headers.ContainsKey(IntegrationEventHeaders.DomainEventId)) is { } returned)
        {
            throw new InvalidOperationException(
                $"A step of the Saga {step.Saga.Name} returned {returned.Message!.GetType().FullName}, which Wolverine would send without the Kit's headers. A step returns only its saga and its timeouts; it emits an Integration Event with PublishAsync."
            );
        }

        return inner.SaveChangesAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    // The step's timeouts are among the envelopes it stored in this transaction and has not sent, in the order it
    // scheduled them, which Wolverine appends each to; a rerun after a rolled-back attempt counts the same ordinals.
    private void KeepTimeouts(PouryaKitSaga state)
    {
        if (step.Input is not { } scheduling)
        {
            return;
        }

        Dictionary<string, int> ordinals = [];
        foreach (var scheduled in step.Delivery.Outstanding.Where(envelope => envelope.Message is TimeoutMessage))
        {
            var wireName = scheduled.Message!.GetType().ToMessageTypeName();
            var ordinal = ordinals.GetValueOrDefault(wireName);
            ordinals[wireName] = ordinal + 1;
            state.Timeouts.TryAdd(scheduled.Id, SagaStorage.TimeoutId(scheduling.StepId, wireName, ordinal));
        }
    }
}
