using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PouryaKit.Cqrs;

namespace PouryaKit.Persistence.EFCore;

// Hands out the connection and transaction of the scope's Unit of Work, the Module's own in a modular Host, so a plain
// SQL write commits or rolls back with what the Unit of Work saves.
internal sealed class EfUnitOfWorkConnection(IUnitOfWork unitOfWork, Operation operation) : IUnitOfWorkConnection
{
    public (DbConnection Connection, DbTransaction Transaction) Join()
    {
        // A query nested in a command, or a nested command's check, runs inside the active transaction, so the
        // transaction alone cannot tell that a write there would escape the Operation's outcome.
        if (!operation.MayChangeState)
        {
            throw new InvalidOperationException(
                "A query, an authorizer or a validator asked to write through the Unit of Work. They only read; write with plain SQL from a command's handler or a Domain Event Handler."
            );
        }

        if (unitOfWork is not EfUnitOfWork ef)
        {
            throw new InvalidOperationException(
                $"Joining the Unit of Work's connection needs the EF Core Unit of Work, but the scope's Unit of Work is {unitOfWork.GetType().Name}."
            );
        }

        var database = ef.DbContext.Database;
        return database.CurrentTransaction is { } transaction
            ? (database.GetDbConnection(), transaction.GetDbTransaction())
            : throw new InvalidOperationException(
                "No Unit of Work is active, so there is no transaction to write in. Write with plain SQL from a command's handler or a Domain Event Handler."
            );
    }
}
