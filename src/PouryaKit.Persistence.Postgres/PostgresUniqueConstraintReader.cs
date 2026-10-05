using Microsoft.EntityFrameworkCore;
using Npgsql;
using PouryaKit.Persistence.EFCore;

namespace PouryaKit.Persistence.Postgres;

// PostgreSQL reports a unique violation as SQLSTATE 23505 with the violated constraint's or unique index's name.
internal sealed class PostgresUniqueConstraintReader : IUniqueConstraintReader
{
    public string? ViolatedConstraint(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } violation
            ? violation.ConstraintName
            : null;
    }
}
