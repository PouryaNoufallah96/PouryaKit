using Microsoft.EntityFrameworkCore;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Persistence.EFCore;

// The Unit of Work over one DbContext, in the order ADR 0003 and ADR 0004 fixed: begin a transaction, run the command,
// check its Result and the Operation, drain the Domain Events of every tracked aggregate, check the Operation again,
// prepare persistence, write the handled Domain Events and the Snapshots of the changed aggregates to the Outbox, check
// the Operation once more, save, commit, and only then send them; otherwise roll back, which leaves nothing in the
// Outbox. A save the database refuses for a concurrent change, or for a unique constraint the Module mapped, rolls back
// and fails the Operation with an Error. Nothing retries: a retry needs a new scope.
internal sealed class EfUnitOfWork(
    DbContext dbContext,
    Operation operation,
    DomainEventDrain domainEvents,
    Func<OperationContext> context,
    IUniqueConstraintReader? uniqueConstraints,
    IEnumerable<UniqueConstraintError> uniqueConstraintErrors,
    IUnitOfWorkOutbox? outbox
) : IUnitOfWork, IReadingStateGuard
{
    /// <summary>The DbContext the Operation's repositories track their changes in.</summary>
    public DbContext DbContext => dbContext;

    /// <summary>The Operation this Unit of Work belongs to.</summary>
    public Operation Operation => operation;

    IDisposable IReadingStateGuard.OnlyReading(string step) => new ReadingState(TrackedState.Capture(dbContext, includeQueryLoads: true), step);

    private sealed class ReadingState(TrackedState state, string step) : IDisposable
    {
        public void Dispose()
        {
            using (state)
            {
                state.ThrowIfChanged(char.ToUpperInvariant(step[0]) + step[1..]);
            }
        }
    }

    public async Task<Result> RunAsync(Func<CancellationToken, Task<Result>> command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Disposing an uncommitted transaction rolls it back. A commit exception can instead mean the database
        // committed but its acknowledgement was lost, leaving the outcome unknown to the caller (ADR 0004).
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var result = await command(cancellationToken);
        if (result.IsSuccess && !operation.IsRollbackOnly)
        {
            var handled = await domainEvents.RunAsync(
                () => dbContext.ChangeTracker.Entries<IAggregateRoot>().Select(entry => entry.Entity),
                cancellationToken
            );

            // Checked again: a Domain Event Handler may have failed, or ignored a nested command that failed.
            if (!operation.IsRollbackOnly)
            {
                var changed = PersistencePreparation.Run(dbContext, context);
                if (outbox is not null)
                {
                    await outbox.WriteAsync(dbContext, handled, changed, cancellationToken);
                }

                // Checked again: writing the Outbox runs the Snapshot mappers, which may have ignored a nested command
                // that failed.
                if (!operation.IsRollbackOnly)
                {
                    if (await SaveAsync(cancellationToken) is { } refused)
                    {
                        result = refused;
                    }
                    else
                    {
                        await transaction.CommitAsync(cancellationToken);
                        if (outbox is not null)
                        {
                            await outbox.FlushAsync();
                        }

                        return result;
                    }
                }
            }
        }

        await transaction.RollbackAsync(CancellationToken.None);
        return result.IsFailure ? result : operation.RollbackErrors;
    }

    // Null when saved; the Error when the database refused the save for a reason the Operation's caller can act on.
    private async Task<Error?> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return UnitOfWorkErrors.ConcurrencyConflict;
        }
        catch (DbUpdateException exception)
            when (uniqueConstraints?.ViolatedConstraint(exception) is { } constraint
                && uniqueConstraintErrors.FirstOrDefault(error => error.Constraint == constraint) is { } mapped)
        {
            return mapped.Error;
        }
    }
}
