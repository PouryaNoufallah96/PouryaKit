using JasperFx;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence.EFCore;
using Wolverine.Marten;
using Wolverine.Runtime;

namespace M2.Marten;

// The Unit of Work over one Marten session, in the order ADR 0003 fixes for the EF one: begin the session's transaction,
// run the command, check its Result and the Operation, drain the Domain Events of every Stream the Operation loaded or
// started, check again, append each Stream's drained events at the version it was read at (FetchForWriting), write the
// handled Domain Events and the published Integration Events through Wolverine's Marten outbox, check once more, and save,
// which commits and then flushes in Wolverine's listener. Otherwise roll back. A stream that moved since it was read, or a
// stream started twice, is the Kit's concurrency Conflict.
internal sealed class MartenUnitOfWork(
    IDocumentStore store,
    Operation operation,
    DomainEventDrain domainEvents,
    IWolverineRuntime runtime,
    StoreNeutralOutbox outbox,
    IServiceProvider scope
) : IUnitOfWork, IReadingStateGuard, IStreams, IAsyncDisposable
{
    private readonly List<Tracked> _tracked = [];
    private IDocumentSession? _session;

    // Opened for the Operation's tenant on first use: Marten's conjoined tenancy keys every event and document by it.
    internal IDocumentSession Session => _session ??= scope.GetRequiredService<OperationContext>().Tenant is { } tenant
        ? store.LightweightSession(tenant)
        : store.LightweightSession();

    // The session whose transaction the Unit of Work holds, for the Receipts and the idempotency store.
    internal IDocumentSession ActiveSession =>
        operation.IsUnitOfWorkActive && _session is { } session
            ? session
            : throw new InvalidOperationException("No Marten Unit of Work is active, so there is no transaction to write in.");

    public async Task<Result> RunAsync(Func<CancellationToken, Task<Result>> command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Begun now, not at the save: a Receipt or an idempotency key written before the handler runs is in it.
        await Session.BeginTransactionAsync(cancellationToken);
        var result = await command(cancellationToken);
        if (result.IsSuccess && !operation.IsRollbackOnly)
        {
            var handled = await domainEvents.RunAsync(TakeRaised, cancellationToken);
            if (!operation.IsRollbackOnly)
            {
                try
                {
                    foreach (var tracked in _tracked.Where(tracked => tracked.Pending.Count > 0))
                    {
                        await tracked.AppendAsync(Session, cancellationToken);
                    }

                    // A new outbox for every attempt, as the EF Unit of Work's: its listener flushes after the commit.
                    await outbox.WriteAsync(new MartenOutbox(runtime, Session), handled, cancellationToken);
                    if (!operation.IsRollbackOnly)
                    {
                        await Session.SaveChangesAsync(cancellationToken);
                        return result;
                    }
                }
                catch (Exception exception) when (exception is ConcurrencyException or global::Marten.Exceptions.ExistingStreamIdCollisionException or ExistingStreamIdCollisionException)
                {
                    result = UnitOfWorkErrors.ConcurrencyConflict;
                }
            }
        }

        // Disposing the session rolls its transaction back; nothing reaches the store or the outbox.
        await DisposeAsync();
        return result.IsFailure ? result : operation.RollbackErrors;
    }

    // Called by the drain at the start of each round, before it takes and clears the raised Domain Events: each Stream's
    // are kept for its append.
    private List<IAggregateRoot> TakeRaised()
    {
        foreach (var tracked in _tracked)
        {
            tracked.Pending.AddRange(tracked.Stream.DomainEvents);
        }

        return [.. _tracked.Select(tracked => tracked.Stream)];
    }

    public async Task<T?> LoadAsync<T>(Guid id, CancellationToken cancellationToken)
        where T : StreamAggregate
    {
        if (_tracked.FirstOrDefault(tracked => tracked.Stream.Id == id) is { Stream: T known })
        {
            return known;
        }

        var stream = await Session.Events.AggregateStreamAsync<T>(id, token: cancellationToken);
        if (stream is not null)
        {
            _tracked.Add(Tracked.Of(stream, stream.Version));
        }

        return stream;
    }

    public void Start<T>(T stream)
        where T : StreamAggregate
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!operation.MayChangeState || !operation.IsUnitOfWorkActive)
        {
            throw new InvalidOperationException($"{typeof(T).Name} {stream.Id} was started where no command's Unit of Work would commit it.");
        }

        _tracked.Add(Tracked.Of(stream, expectedVersion: 0));
    }

    // What a step that only reads could change that a commit would persist: a Stream started, an event raised on a loaded
    // or started Stream, or an operation queued on the session. An in-place change that raises no event is never appended,
    // so it never persists.
    IDisposable IReadingStateGuard.OnlyReading(string step) => new ReadingState(this, step, Snapshot());

    private (int Streams, int Raised, int Queued) Snapshot() =>
        (
            _tracked.Count(tracked => tracked.ExpectedVersion == 0),
            _tracked.Sum(tracked => tracked.Stream.DomainEvents.Count + tracked.Pending.Count),
            _session is null ? 0 : _session.PendingChanges.Operations().Count() + _session.PendingChanges.Streams().Count
        );

    private sealed class ReadingState(MartenUnitOfWork unitOfWork, string step, (int, int, int) before) : IDisposable
    {
        public void Dispose()
        {
            if (unitOfWork.Snapshot() != before)
            {
                throw new InvalidOperationException($"{char.ToUpperInvariant(step[0])}{step[1..]} changed a Stream, which only a command may change.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is { } session)
        {
            _session = null;
            await session.DisposeAsync();
        }
    }

    private sealed record Tracked(StreamAggregate Stream, long ExpectedVersion, List<DomainEvent> Pending, Func<IDocumentSession, Guid, long, IEnumerable<object>, CancellationToken, Task> Append)
    {
        public static Tracked Of<T>(T stream, long expectedVersion)
            where T : StreamAggregate =>
            new(
                stream,
                expectedVersion,
                [],
                static async (session, id, version, events, cancellationToken) =>
                {
                    // At the version the Operation read, 0 for a new stream: a stream that moved, or exists already, is a
                    // ConcurrencyException here or at the save. Never Append(id, expectedVersion, ...), which needs Rich mode.
                    var writing = await session.Events.FetchForWriting<T>(id, version, cancellationToken);
                    writing.AppendMany(events);
                }
            );

        public Task AppendAsync(IDocumentSession session, CancellationToken cancellationToken) =>
            Append(session, Stream.Id, ExpectedVersion, Pending, cancellationToken);
    }
}
