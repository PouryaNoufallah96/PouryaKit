using PouryaKit.Cqrs;
using PouryaKit.Persistence.Marten;

namespace PouryaKit.Messaging.Wolverine.Marten;

// A Stream Module's Receipts and Snapshot versions are rows of its own schema's tables, written with the EF Module's
// statements on its Marten Unit of Work's transaction, which begins before the delivery's handler runs: a concurrent second
// delivery of one message waits on the first one's row, then records nothing and never runs its handler, and a Receipt
// commits with the handler's Stream effect or not at all. Marten documents would be written only at the save (ADR 0011).
internal sealed class MartenReceipts(IUnitOfWork unitOfWork) : IReceipts
{
    public async Task<bool> RecordAsync(string subscription, Guid messageId, CancellationToken cancellationToken) =>
        await MartenUnitOfWorkTransaction.ExecuteAsync(
            unitOfWork,
            ReceiptStatements.Record,
            ReceiptStatements.ReceiptTable,
            [subscription, messageId],
            cancellationToken
        ) == 1;

    public async Task<bool> RecordVersionAsync(string subscription, string aggregateId, long version, CancellationToken cancellationToken) =>
        await MartenUnitOfWorkTransaction.ExecuteAsync(
            unitOfWork,
            ReceiptStatements.RecordVersion,
            ReceiptStatements.SnapshotVersionTable,
            [subscription, aggregateId, version],
            cancellationToken
        ) == 1;
}
