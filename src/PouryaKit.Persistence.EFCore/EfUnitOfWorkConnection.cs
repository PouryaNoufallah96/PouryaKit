using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PouryaKit.Cqrs;

namespace PouryaKit.Persistence.EFCore;

// Hands out the connection and transaction of the scope's Unit of Work, the Module's own in a modular Host, so a plain
// SQL write commits or rolls back with what the Unit of Work saves.
internal sealed class EfUnitOfWorkConnection(IUnitOfWork unitOfWork) : IUnitOfWorkConnection
{
    public (DbConnection Connection, DbTransaction Transaction) Join()
    {
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
                "No Unit of Work is active, so there is no transaction to write in. Write from a command's handler: a query runs without a Unit of Work."
            );
    }
}
