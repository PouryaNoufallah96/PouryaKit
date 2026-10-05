using Microsoft.EntityFrameworkCore;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Persistence.EFCore;

namespace PouryaKit.Messaging.Wolverine.EFCore;

// A Receipt is a row in the receiving Module's own schema, written on its Unit of Work's transaction, so it commits
// with the handler's effect or not at all. The primary key makes a second Receipt for the same message wait for the
// first transaction, then insert nothing.
internal sealed class EfReceipts(IUnitOfWork unitOfWork) : IReceipts
{
    public async Task<bool> RecordAsync(string subscription, Guid messageId, CancellationToken cancellationToken)
    {
        var dbContext =
            unitOfWork.DbContext
            ?? throw new InvalidOperationException(
                $"Receipts are kept in the EF Core Unit of Work's transaction, but the scope's Unit of Work is {unitOfWork.GetType().Name}."
            );
        // The table name comes from the Module's model, never from input; the values are parameters.
        var table = dbContext.Model.GetDefaultSchema() is { } schema
            ? $"\"{schema}\".{ReceiptTable.Name}"
            : ReceiptTable.Name;
        var insert = $"insert into {table} (subscription, message_id) values ({{0}}, {{1}}) on conflict do nothing";
        try
        {
            return await dbContext.Database.ExecuteSqlRawAsync(insert, [subscription, messageId], cancellationToken) == 1;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            throw new InvalidOperationException(
                $"{dbContext.GetType().Name} has no {table} table to keep Receipts in. Add a migration to the Module that calls CreateReceiptTable with its schema.",
                exception
            );
        }
    }
}
