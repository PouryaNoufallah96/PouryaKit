using PouryaKit.Domain;

namespace PouryaKit.Persistence.Postgres;

/// <summary>
/// Gap-free numbers per scope key, such as one invoice number sequence per year, taken in the Operation's Unit of Work.
/// A Module registers it with <c>AddPouryaKitNumberSequences</c> and creates its table with
/// <see cref="NumberSequenceTable"/>.
/// </summary>
public interface INumberSequences
{
    /// <summary>
    /// The next number of <paramref name="scopeKey"/>, starting at 1, taken on the active Unit of Work's transaction:
    /// a rollback gives it back, so the next Operation receives it. Operations that ask for a number of one scope key
    /// run one at a time from the request until their commit or rollback; the second waits. Fails with
    /// <c>UnitOfWorkErrors.ConcurrencyConflict</c> when PostgreSQL ends a deadlock by aborting this Operation's wait,
    /// such as two Operations taking numbers of two scope keys in opposite orders; nothing was committed, and the
    /// Operation may run again in a new scope. Use it only from a command's handler or a Domain Event Handler: it
    /// throws <see cref="InvalidOperationException"/> in code that only reads, where no Unit of Work transaction is
    /// active, and when the Module has no number sequence table.
    /// </summary>
    Task<Result<long>> NextAsync(string scopeKey, CancellationToken cancellationToken);
}
