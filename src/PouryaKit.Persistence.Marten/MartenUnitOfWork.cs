using System.Globalization;
using JasperFx;
using Marten;
using Microsoft.Extensions.Logging;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using Weasel.Postgresql;
using IChangeSet = Marten.Services.IChangeSet;
using JasperFxStreamCollision = JasperFx.Events.ExistingStreamIdCollisionException;
using MartenStreamCollision = Marten.Exceptions.ExistingStreamIdCollisionException;
using SessionOptions = Marten.Services.SessionOptions;

namespace PouryaKit.Persistence.Marten;

// The Unit of Work over one Marten session, in the order ADR 0003 and ADR 0011 fix: begin its own transaction before the
// command and open the session over it, run the command, check its Result and the Operation, drain the Domain Events of
// every Stream the Operation loaded or started, check the Operation again, append each Stream's drained events at the
// version it was read at, write the handled Domain Events through the Outbox, and save, which commits and then sends them
// in Wolverine's session listener; otherwise roll back, which leaves nothing in the store or the Outbox. A stream that moved
// since it was read, or one started twice, fails the Operation with the Kit's concurrency Conflict. Nothing retries.
internal sealed partial class MartenUnitOfWork(
    IDocumentStore store,
    Operation operation,
    DomainEventDrain domainEvents,
    Func<OperationContext> context,
    IStreamOutbox? outbox,
    ILogger<MartenUnitOfWork> logger,
    string schema
) : IUnitOfWork, IReadingStateGuard, IStreams, IAsyncDisposable, IDisposable
{
    private readonly List<Tracked> _tracked = [];
    private IDocumentSession? _session;
    private NpgsqlConnection? _connection;
    private NpgsqlTransaction? _transaction;
    private bool _committed;

    // Outside a command, a session of the Operation's tenant for reads; in one, the session over the transaction.
    private IDocumentSession Session => _session ??= Tenant() is { } tenant ? store.LightweightSession(tenant) : store.LightweightSession();

    public async Task<Result> RunAsync(Func<CancellationToken, Task<Result>> command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await BeginAsync(cancellationToken);
        try
        {
            var result = await command(cancellationToken);
            if (result.IsSuccess && !operation.IsRollbackOnly)
            {
                var handled = await domainEvents.RunAsync(TakeRaised, cancellationToken);

                // Checked again: a Domain Event Handler may have failed, or ignored a nested command that failed.
                if (!operation.IsRollbackOnly)
                {
                    if (await SaveAsync(handled, cancellationToken) is not { } refused)
                    {
                        return result;
                    }

                    result = refused;
                }
            }

            return result.IsFailure ? result : operation.RollbackErrors;
        }
        finally
        {
            // Disposing a transaction that did not commit rolls it back.
            await DisposeAsync();
        }
    }

    // Begun before the command, so a Receipt or an idempotency key written before the handler runs is in the
    // transaction, and a concurrent duplicate's waits for it. Marten commits the transaction when it saves work and
    // leaves it to the Unit of Work otherwise. A session a check opened to read is replaced; what it loaded stays tracked.
    private async Task BeginAsync(CancellationToken cancellationToken)
    {
        var tenant = Tenant();
        await DisposeAsync();
        _connection = await ((PostgresqlDatabase)store.Storage.Database).DataSource.OpenConnectionAsync(cancellationToken);
        _transaction = await _connection.BeginTransactionAsync(cancellationToken);
        var options = SessionOptions.ForTransaction(_transaction, shouldAutoCommit: true);
        if (tenant is not null)
        {
            options.TenantId = tenant;
        }

        // Before the listener Wolverine's outbox adds, so the commit is known when that listener sends.
        options.Listeners.Add(new Committed(this));
        _session = store.LightweightSession(options);
        _committed = false;
    }

    // Null when committed; the Error when a concurrent change to a Stream refused the append.
    private async Task<Error?> SaveAsync(IReadOnlyList<DomainEvent> handled, CancellationToken cancellationToken)
    {
        var session = _session!;
        try
        {
            foreach (var tracked in _tracked.Where(tracked => tracked.Pending.Count > 0))
            {
                await tracked.AppendAsync(session, cancellationToken);
            }

            if (outbox is not null)
            {
                await outbox.WriteAsync(session, handled, cancellationToken);
            }

            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (!_committed && exception is ConcurrencyException or MartenStreamCollision or JasperFxStreamCollision)
        {
            return UnitOfWorkErrors.ConcurrencyConflict;
        }
        catch (Exception exception) when (_committed)
        {
            // A listener after Marten's commit failed, such as Wolverine's send: the change committed, and reporting a
            // failure now would invite the caller to make it twice. What was not sent stays in the message store.
            LogNotSent(logger, exception);
        }

        // Marten saves and commits nothing when the session holds no work, as when a Receipt or an idempotency key is the
        // Operation's only write; those rows still commit. A commit exception here can leave the outcome unknown (ADR 0004).
        if (!_committed)
        {
            await _transaction!.CommitAsync(cancellationToken);
        }

        return null;
    }

    // Marten calls its session's listeners after its commit only, so only when it had work to save.
    private sealed class Committed(MartenUnitOfWork unitOfWork) : DocumentSessionListenerBase
    {
        public override Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
        {
            unitOfWork._committed = true;
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

    // Marten keeps *DEFAULT* for the streams of no tenant, and OperationContext refuses only a blank tenant, so a tenant of
    // that id would read and write the tenantless streams.
    private string? Tenant() =>
        context().Tenant is var tenant && tenant == StorageConstants.DefaultTenantId
            ? throw new InvalidOperationException(
                $"The Operation's tenant is {StorageConstants.DefaultTenantId}, the id Marten keeps for the Streams of no tenant, so its Streams would be those of every Operation without one. Give the tenant another id."
            )
            : tenant;

    public async Task<TStream?> LoadAsync<TStream>(TypedId<Guid> id, CancellationToken cancellationToken)
        where TStream : StreamAggregate
    {
        ArgumentNullException.ThrowIfNull(id);
        if (_tracked.FirstOrDefault(tracked => tracked.Id == id.Value) is { } known)
        {
            return known.Stream as TStream
                ?? throw new InvalidOperationException($"The stream {id.Value} is a {known.Stream.GetType().Name}, not a {typeof(TStream).Name}.");
        }

        var stream = await Session.Events.AggregateStreamAsync<TStream>(id.Value, token: cancellationToken);
        if (stream is not null)
        {
            _tracked.Add(Tracked.Of(stream, stream.Version));
        }

        return stream;
    }

    public void Start<TStream>(TStream stream)
        where TStream : StreamAggregate
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!operation.MayChangeState || !operation.IsUnitOfWorkActive)
        {
            throw new InvalidOperationException(
                $"{typeof(TStream).Name} {stream.Id} was started where no command's Unit of Work would commit it. Start a Stream in a command's handler or a Domain Event Handler."
            );
        }

        _tracked.Add(Tracked.Of(stream, expectedVersion: 0));
    }

    // What a step that only reads could change that the commit would append: a Stream started, an event raised on a
    // tracked Stream, the identity of a tracked Stream, or an operation queued on the session. Any other change to a Stream
    // raises no event, so it is never appended.
    IDisposable IReadingStateGuard.OnlyReading(string step) => new ReadingState(this, step, Snapshot());

    private (int Started, int Raised, int Queued, bool SameIdentities) Snapshot() =>
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

    // A Kit store's statement on the Unit of Work's own connection and transaction, over the Module's table, its values
    // bound in order as Npgsql's positional parameters.
    internal NpgsqlCommand Command(Func<string, string> statement, string table, object[] values)
    {
        if (!operation.IsUnitOfWorkActive || _transaction is null)
        {
            throw new InvalidOperationException("No Marten Unit of Work is active, so there is no transaction to write in.");
        }

        return Statements.Command(statement($"\"{schema.Replace("\"", "\"\"", StringComparison.Ordinal)}\".{table}"), values, _connection!, _transaction);
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

    public void Dispose()
    {
        _session?.Dispose();
        _transaction?.Dispose();
        _connection?.Dispose();
        (_session, _transaction, _connection) = (null, null, null);
    }

    [LoggerMessage(
        EventName = "NotSent",
        Level = LogLevel.Warning,
        Message = "Notifications and Integration Events committed with the Operation's Streams could not be sent; the message store recovers them after the inbox stale time"
    )]
    private static partial void LogNotSent(ILogger logger, Exception exception);

    // Id is the stream's identity as the Unit of Work began tracking it, which the append uses.
    private sealed record Tracked(StreamAggregate Stream, Guid Id, long ExpectedVersion, List<DomainEvent> Pending, Func<IDocumentSession, Tracked, CancellationToken, Task> Append)
    {
        public static Tracked Of<TStream>(TStream stream, long expectedVersion)
            where TStream : StreamAggregate =>
            new(
                stream,
                stream.Id,
                expectedVersion,
                [],
                static async (session, tracked, cancellationToken) =>
                {
                    // At the version the Operation read, 0 for a new stream: one that moved since, or exists already, throws
                    // here or at the save. Never Append(id, expectedVersion, ...), which Marten's default Quick mode refuses.
                    var writing = await session.Events.FetchForWriting<TStream>(tracked.Id, tracked.ExpectedVersion, cancellationToken);
                    writing.AppendMany(tracked.Pending);
                }
            );

        public Task AppendAsync(IDocumentSession session, CancellationToken cancellationToken) => Append(session, this, cancellationToken);
    }
}

// Npgsql's positional parameters for a Kit statement written with composite-format items, as EF Core binds them.
internal static class Statements
{
    public static NpgsqlCommand Command(string sql, object[] values, NpgsqlConnection? connection = null, NpgsqlTransaction? transaction = null)
    {
        var command = new NpgsqlCommand(
            string.Format(CultureInfo.InvariantCulture, sql, [.. values.Select((_, index) => (object)$"${index + 1}")]),
            connection,
            transaction
        );
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        return command;
    }
}
