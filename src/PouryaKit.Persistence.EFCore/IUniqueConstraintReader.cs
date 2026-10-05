using Microsoft.EntityFrameworkCore;

namespace PouryaKit.Persistence.EFCore;

/// <summary>
/// Reads which unique constraint a refused save violated, as only the database package can. The Unit of Work turns a
/// violation of a constraint the Module mapped with <c>MapUniqueConstraint</c> into the Module's Error; a database
/// package such as <c>PouryaKit.Persistence.Postgres</c> registers it, and Feature code never uses it.
/// </summary>
public interface IUniqueConstraintReader
{
    /// <summary>
    /// The name of the unique constraint or unique index whose violation refused the save, or null when the save
    /// failed for any other reason.
    /// </summary>
    string? ViolatedConstraint(DbUpdateException exception);
}
