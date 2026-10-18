using PouryaKit.Domain;

namespace PouryaKit.Persistence;

/// <summary>The Errors a Unit of Work fails an Operation with, whichever write model it commits.</summary>
public static class UnitOfWorkErrors
{
    /// <summary>
    /// Another Operation committed a change to an aggregate this Operation changed, after this one read it; or the
    /// Operation wrote an aggregate it attached rather than loaded, whose stored row has another version or no longer
    /// exists; or, in <c>PouryaKit.Persistence.Postgres</c>, PostgreSQL ended a deadlock by aborting this Operation's wait
    /// for a number sequence; or, in <c>PouryaKit.Persistence.Marten</c>, another Operation appended to a Stream this
    /// Operation changed after this one read it, or started a Stream this Operation started too. Nothing was committed;
    /// the caller decides whether to run the Operation again, in a new scope.
    /// </summary>
    public static readonly Error ConcurrencyConflict = Error.Conflict(
        "UnitOfWork.ConcurrencyConflict",
        "Another operation changed the same data first, so nothing was saved."
    );
}
