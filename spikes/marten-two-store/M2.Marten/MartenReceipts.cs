using PouryaKit.Cqrs;
using PouryaKit.Messaging.Wolverine;

namespace M2.Marten;

// EfReceipts' statements on the Marten Unit of Work's session, in the transaction it began before the command ran: the
// primary key makes a concurrent second Receipt for the same message wait for the first transaction, then insert nothing,
// before its handler runs. Rolled back with the session, so a Receipt commits with the Stream effect or not at all.
internal sealed class MartenReceipts(IUnitOfWork unitOfWork, string schema) : IReceipts
{
    public async Task<bool> RecordAsync(string subscription, Guid messageId, CancellationToken cancellationToken) =>
        (
            await Session().AdvancedSql.QueryAsync<int>(
                $"insert into {schema}.{MartenTables.Receipts} (subscription, message_id) values (?, ?) on conflict do nothing returning 1",
                cancellationToken,
                subscription,
                messageId
            )
        ).Count == 1;

    public async Task<bool> RecordVersionAsync(string subscription, string aggregateId, long version, CancellationToken cancellationToken) =>
        (
            await Session().AdvancedSql.QueryAsync<int>(
                $"insert into {schema}.{MartenTables.SnapshotVersions} as applied (subscription, aggregate_id, version) values (?, ?, ?) on conflict (subscription, aggregate_id) do update set version = excluded.version where applied.version < excluded.version returning 1",
                cancellationToken,
                subscription,
                aggregateId,
                version
            )
        ).Count == 1;

    private global::Marten.IDocumentSession Session() =>
        (unitOfWork as MartenUnitOfWork
            ?? throw new InvalidOperationException($"Marten Receipts are kept in a Marten Unit of Work, but the scope's is {unitOfWork.GetType().Name}.")
        ).ActiveSession;
}
