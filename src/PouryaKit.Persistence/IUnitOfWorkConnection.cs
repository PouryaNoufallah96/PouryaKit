using System.Data.Common;

namespace PouryaKit.Persistence;

/// <summary>
/// The connection and transaction of the Operation's active Unit of Work, for a Module-internal write with plain SQL,
/// such as Dapper, that must commit or roll back with the Operation. Use it only from a command's handler or a Domain
/// Event Handler, and pass both to every statement:
/// <c>connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: ct))</c>.
/// A query never writes, and neither does an authorizer or a validator. Write only to the Module's own non-aggregate
/// tables, such as projections and read models: an aggregate changes only through its repository, or its version and
/// Snapshots are bypassed.
/// </summary>
public interface IUnitOfWorkConnection
{
    /// <summary>
    /// The active Unit of Work's connection and transaction. They belong to the Unit of Work: never dispose, commit or
    /// roll back either. Throws <see cref="InvalidOperationException"/> in a query, an authorizer or a validator at any
    /// depth, including a query sent from a command's handler, and whenever no Unit of Work transaction is active, so a
    /// write can never land outside the Operation's outcome.
    /// </summary>
    (DbConnection Connection, DbTransaction Transaction) Join();
}
