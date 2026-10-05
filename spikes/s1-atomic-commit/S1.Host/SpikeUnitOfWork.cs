using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace S1;

/// <summary>Where the operation ended: the Spike's stand-in for a Result.</summary>
public enum Outcome { Succeeded, Failed }

/// <summary>Points the tests can stop at, to observe the database from another connection or kill the process.</summary>
public sealed class CommitHooks
{
    /// <summary>After SaveChanges, before commit, given the Operation's DbContext. Throwing here rolls the Operation back.</summary>
    public Func<DbContext, Task>? AfterSaveBeforeCommit { get; init; }

    /// <summary>After commit, before the outbox is flushed to the sending agents.</summary>
    public Func<Task>? AfterCommitBeforeFlush { get; init; }

    /// <summary>Deliberately flush the outbox after a rollback, to observe what Wolverine does with it.</summary>
    public bool FlushEvenAfterRollback { get; init; }
}

/// <summary>
/// The Spike's owned Unit of Work. Feature code (the operation delegate) only mutates the DbContext and
/// publishes through the outbox; this class alone begins, saves, commits or rolls back, and flushes.
/// It deliberately does not call <see cref="IDbContextOutbox{T}.SaveChangesAndFlushMessagesAsync(CancellationToken)"/>,
/// which would hide the commit inside Wolverine.
/// </summary>
public sealed class SpikeUnitOfWork<TDbContext>(IDbContextOutbox<TDbContext> outbox) where TDbContext : DbContext
{
    public TDbContext DbContext => outbox.DbContext;

    public async Task<Outcome> ExecuteAsync(
        Func<TDbContext, IDbContextOutbox<TDbContext>, Task<Outcome>> operation,
        CommitHooks? hooks = null,
        CancellationToken ct = default)
    {
        hooks ??= new CommitHooks();
        var db = outbox.DbContext;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var committed = false;
        try
        {
            var outcome = await operation(db, outbox);
            if (outcome == Outcome.Failed)
            {
                await tx.RollbackAsync(ct);
                return Outcome.Failed;
            }

            await db.SaveChangesAsync(ct);
            if (hooks.AfterSaveBeforeCommit is not null) await hooks.AfterSaveBeforeCommit(db);

            await tx.CommitAsync(ct);
            committed = true;
        }
        catch
        {
            if (!committed) await tx.RollbackAsync(CancellationToken.None);
            if (hooks.FlushEvenAfterRollback) await outbox.FlushOutgoingMessagesAsync();
            throw;
        }

        if (hooks.AfterCommitBeforeFlush is not null) await hooks.AfterCommitBeforeFlush();
        await outbox.FlushOutgoingMessagesAsync();
        return Outcome.Succeeded;
    }
}
