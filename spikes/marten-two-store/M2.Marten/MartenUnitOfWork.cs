using JasperFx;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence.EFCore;
using Weasel.Postgresql;
using Wolverine.Marten;
using Wolverine.Runtime;

namespace M2.Marten;

// The Unit of Work over one Marten session, in the order ADR 0003 fixes for the EF one: begin its own transaction, run the
// command, check its Result and the Operation, drain the Domain Events of every Stream the Operation loaded or started,
// check again, append each Stream's drained events at the version it was read at (FetchForWriting), write the handled
// Domain Events and the published Integration Events through Wolverine's Marten outbox, check once more, and save, which
// commits and then flushes in Wolverine's listener; Marten saves nothing and commits nothing when the Operation queued no
// work, as when a Receipt or an idempotency key is its only write, so the Unit of Work then commits its transaction itself.
// Otherwise roll back. A stream that moved since it was read, or a stream started twice, is the Kit's concurrency Conflict.
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
    private NpgsqlConnection? _connection;
    private NpgsqlTransaction? _transaction;
    private bool _savedByMarten;

    private string? Tenant => scope.GetRequiredService<OperationContext>().Tenant;

    // Outside a command, a session of its own for the Operation's tenant: Marten's conjoined tenancy keys every event and
    // document by it.
    internal IDocumentSession Session => _session ??= Tenant is { } tenant ? store.LightweightSession(tenant) : store.LightweightSession();

    // The session over the Unit of Work's transaction, for the Receipts and the idempotency store.
    internal IDocumentSession ActiveSession =>
        operation.IsUnitOfWorkActive && _transaction is not null && _session is { } session
            ? session
            : throw new InvalidOperationException("No Marten Unit of Work is active, so there is no transaction to write in.");

    public async Task<Result> RunAsync(Func<CancellationToken, Task<Result>> command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Begun now, not at the save: a Receipt or an idempotency key written before the handler runs is in it.
        await BeginAsync(cancellationToken);
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
                        // Still open when Marten had no work to save: nothing to flush, but the rows written must commit.
                        if (!_savedByMarten)
                        {
                            await _transaction!.CommitAsync(cancellationToken);
                        }

                        return result;
                    }
                }
                catch (Exception exception) when (exception is ConcurrencyException or global::Marten.Exceptions.ExistingStreamIdCollisionException or ExistingStreamIdCollisionException)
                {
                    result = UnitOfWorkErrors.ConcurrencyConflict;
                }
            }
        }

        // Disposing the transaction rolls it back; nothing reaches the store or the outbox.
        await DisposeAsync();
        return result.IsFailure ? result : operation.RollbackErrors;
    }

    // The Unit of Work owns its connection and transaction, as the EF one owns its DbContext's: Marten commits it when it
    // saves work, and leaves it to the Unit of Work otherwise. A session a check opened to read is replaced.
    private async Task BeginAsync(CancellationToken cancellationToken)
    {
        await DisposeAsync();
        _connection = await ((PostgresqlDatabase)store.Storage.Database).DataSource.OpenConnectionAsync(cancellationToken);
        _transaction = await _connection.BeginTransactionAsync(cancellationToken);
        var options = global::Marten.Services.SessionOptions.ForTransaction(_transaction, shouldAutoCommit: true);
        if (Tenant is { } tenant)
        {
            options.TenantId = tenant;
        }

        _session = store.LightweightSession(options);
        _savedByMarten = false;
        _session.Listeners.Add(new SavedByMarten(this));
    }

    // Marten calls its session's listeners after the commit only when it had work to save.
    private sealed class SavedByMarten(MartenUnitOfWork unitOfWork) : DocumentSessionListenerBase
    {
        public override Task AfterCommitAsync(IDocumentSession session, global::Marten.Services.IChangeSet commit, CancellationToken token)
        {
            unitOfWork._savedByMarten = true;
            return Task.CompletedTask;
        }
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
        if (_tracked.FirstOrDefault(tracked => tracked.Id == id) is { Stream: T known })
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
    // or started Stream, the identity of a tracked Stream, or an operation queued on the session. The append goes to the
    // identity captured when tracking began, whatever the Stream's Id says later; any other in-place change that raises no
    // event is never appended, so it never persists.
    IDisposable IReadingStateGuard.OnlyReading(string step) => new ReadingState(this, step, Snapshot());

    private (int Streams, int Raised, int Queued, bool SameIdentities) Snapshot() =>
        (
            _tracked.Count(tracked => tracked.ExpectedVersion == 0),
            _tracked.Sum(tracked => tracked.Stream.DomainEvents.Count + tracked.Pending.Count),
            _session is null ? 0 : _session.PendingChanges.Operations().Count() + _session.PendingChanges.Streams().Count,
            _tracked.All(tracked => tracked.Stream.Id == tracked.Id)
        );

    private sealed class ReadingState(MartenUnitOfWork unitOfWork, string step, (int, int, int, bool) before) : IDisposable
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

        if (_transaction is { } transaction)
        {
            _transaction = null;
            await transaction.DisposeAsync();
        }

        if (_connection is { } connection)
        {
            _connection = null;
            await connection.DisposeAsync();
        }
    }

    // Id is the stream's identity as the Unit of Work began tracking it, which the append uses.
    private sealed record Tracked(StreamAggregate Stream, Guid Id, long ExpectedVersion, List<DomainEvent> Pending, Func<IDocumentSession, Guid, long, IEnumerable<object>, CancellationToken, Task> Append)
    {
        public static Tracked Of<T>(T stream, long expectedVersion)
            where T : StreamAggregate =>
            new(
                stream,
                stream.Id,
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
            Append(session, Id, ExpectedVersion, Pending, cancellationToken);
    }
}
