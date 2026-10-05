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
            table => $"insert into {table} (subscription, message_id) values ({{0}}, {{1}}) on conflict do nothing",
            [subscription, messageId],
            cancellationToken
        );

    // Under read committed, a conflicting row another transaction holds makes this one wait, then decide on the row
    // that transaction committed: of two concurrent versions, the newer is applied whichever runs first.
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
            table =>
                $"insert into {table} as applied (subscription, aggregate_id, version) values ({{0}}, {{1}}, {{2}}) on conflict (subscription, aggregate_id) do update set version = excluded.version where applied.version < excluded.version",
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
            unitOfWork.DbContext
            ?? throw new InvalidOperationException(
                $"Receipts are kept in the EF Core Unit of Work's transaction, but the scope's Unit of Work is {unitOfWork.GetType().Name}."
            );
        // The table name comes from the Module's model, never from input; the values are parameters.
        var table = dbContext.Model.GetDefaultSchema() is { } schema ? $"\"{schema}\".{name}" : name;
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
