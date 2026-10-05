using Microsoft.EntityFrameworkCore;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Persistence.EFCore;

// The Unit of Work over one DbContext, in the order ADR 0003 and ADR 0004 fixed: begin a transaction, run the command,
// check its Result and the Operation, drain the Domain Events of every tracked aggregate, check the Operation again,
// save, commit; otherwise roll back. Nothing retries: a retry needs a new scope.
internal sealed class EfUnitOfWork(DbContext dbContext, Operation operation, DomainEventDrain domainEvents) : IUnitOfWork
{
    /// <summary>The DbContext the Operation's repositories track their changes in.</summary>
    public DbContext DbContext => dbContext;

    public async Task<Result> RunAsync(Func<CancellationToken, Task<Result>> command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Disposing a transaction that was neither committed nor rolled back rolls it back, so an exception from the
        // command, a Domain Event Handler, the save or the commit leaves nothing committed.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var result = await command(cancellationToken);
        if (result.IsSuccess && !operation.IsRollbackOnly)
        {
            await domainEvents.RunAsync(
                () => dbContext.ChangeTracker.Entries<IAggregateRoot>().Select(entry => entry.Entity),
                cancellationToken
            );

            // Checked again: a Domain Event Handler may have failed, or ignored a nested command that failed.
            if (!operation.IsRollbackOnly)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
        }

        await transaction.RollbackAsync(CancellationToken.None);
        return result.IsFailure ? result : operation.RollbackErrors;
    }
}
