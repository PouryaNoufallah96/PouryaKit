using Microsoft.EntityFrameworkCore;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Persistence.EFCore;

namespace PouryaKit.Messaging.Wolverine.EFCore;

// A Receipt is a row in the receiving Module's own schema, written on its Unit of Work's transaction, so it commits
// with the handler's effect or not at all. The primary key makes a second Receipt for the same message wait for the
// first transaction, then insert nothing. A Snapshot's version is kept the same way, one row per aggregate, which a
// newer version replaces in one statement.
internal sealed class EfReceipts(IUnitOfWork unitOfWork) : IReceipts
{
    public Task<bool> RecordAsync(string subscription, Guid messageId, CancellationToken cancellationToken) =>
        ExecuteAsync(
            ReceiptTable.Name,
            "Receipts",
            "CreateReceiptTable",
            ReceiptStatements.Record,
            [subscription, messageId],
            cancellationToken
        );

    // Older first allows both in increasing order; newer first skips the older.
    public Task<bool> RecordVersionAsync(
        string subscription,
        string aggregateId,
        long version,
        CancellationToken cancellationToken
    ) =>
        ExecuteAsync(
            SnapshotVersionTable.Name,
            "the versions of the Snapshots its handlers applied",
            "CreateSnapshotVersionTable",
            ReceiptStatements.RecordVersion,
            [subscription, aggregateId, version],
            cancellationToken
        );

    private async Task<bool> ExecuteAsync(
        string name,
        string keeps,
        string creator,
        Func<string, string> sql,
        object[] parameters,
        CancellationToken cancellationToken
    )
    {
        var dbContext =
            EfUnitOfWorkDbContext.Of(unitOfWork)
            ?? throw new InvalidOperationException(
                $"Receipts are kept in the EF Core Unit of Work's transaction, but the scope's Unit of Work is {unitOfWork.GetType().Name}."
            );
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException($"No Unit of Work is active, so there is no transaction to keep {keeps} in.");
        }

        // The table name comes from the Module's model, never from input; the values are parameters.
        var table = dbContext.Model.GetDefaultSchema() is { } schema
            ? $"\"{schema.Replace("\"", "\"\"", StringComparison.Ordinal)}\".{name}"
            : name;
        var statement = sql(table);
        try
        {
            return await dbContext.Database.ExecuteSqlRawAsync(statement, parameters, cancellationToken) == 1;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            throw new InvalidOperationException(
                $"{dbContext.GetType().Name} has no {table} table to keep {keeps} in. Add a migration to the Module that calls {creator} with its schema.",
                exception
            );
        }
    }
}
