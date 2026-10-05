using System.Data.Common;

namespace PouryaKit.Persistence;

/// <summary>
/// The connection and transaction of the Operation's active Unit of Work, for a Module-internal write with plain SQL,
/// such as Dapper, that must commit or roll back with the Operation. Pass both to every statement:
/// <c>connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: ct))</c>.
/// Write only to the Module's own non-aggregate tables, such as projections and read models: an aggregate changes only
/// through its repository, or its version and Snapshots are bypassed.
/// </summary>
public interface IUnitOfWorkConnection
{
    /// <summary>
    /// The active Unit of Work's connection and transaction. They belong to the Unit of Work: never dispose, commit or
    /// roll back either. Throws <see cref="InvalidOperationException"/> when no Unit of Work transaction is active, as
    /// in a query that runs on its own, so a write can never land outside the Operation's transaction. A query sent
    /// from inside a command shares that command's transaction, and may write through it like the command.
    /// </summary>
    (DbConnection Connection, DbTransaction Transaction) Join();
}
