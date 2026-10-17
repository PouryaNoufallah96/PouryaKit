using PouryaKit.Domain;

namespace PouryaKit.Persistence.EFCore;

/// <summary>The Errors the EF Core Unit of Work fails an Operation with.</summary>
public static class UnitOfWorkErrors
{
    /// <summary>
    /// Another Operation committed a change to an aggregate this Operation changed, after this one read it; or the
    /// Operation wrote an aggregate it attached rather than loaded, whose stored row has another version or no longer
    /// exists; or, in <c>PouryaKit.Persistence.Postgres</c>, PostgreSQL ended a deadlock by aborting this Operation's wait
    /// for a number sequence. Nothing was committed; the caller decides whether to run the Operation again, in a new
    /// scope.
    /// </summary>
    public static readonly Error ConcurrencyConflict = Error.Conflict(
        "UnitOfWork.ConcurrencyConflict",
        "Another operation changed the same data first, so nothing was saved."
    );
}
