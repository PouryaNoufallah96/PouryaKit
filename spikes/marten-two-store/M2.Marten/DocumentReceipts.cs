using PouryaKit.Cqrs;
using PouryaKit.Messaging.Wolverine;

namespace M2.Marten;

/// <summary>A Receipt kept as a Marten document, one per subscription and message.</summary>
public sealed class ReceiptDocument
{
    /// <summary>The subscription's name and the message's identity.</summary>
    public string Id { get; set; } = "";
}

// The rejected option, given one honest attempt: Receipts as Marten documents on the same session. A document is written
// only when the session saves, so a concurrent duplicate finds no committed Receipt, runs its handler, and fails at the
// save on the document's primary key; its retry then finds the Receipt and applies nothing.
internal sealed class DocumentReceipts(IUnitOfWork unitOfWork) : IReceipts
{
    public async Task<bool> RecordAsync(string subscription, Guid messageId, CancellationToken cancellationToken)
    {
        var session = ((MartenUnitOfWork)unitOfWork).ActiveSession;
        var id = $"{subscription}:{messageId}";
        if (await session.LoadAsync<ReceiptDocument>(id, cancellationToken) is not null)
        {
            return false;
        }

        session.Insert(new ReceiptDocument { Id = id });
        return true;
    }

    public Task<bool> RecordVersionAsync(string subscription, string aggregateId, long version, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The document attempt covers Receipts only.");
}
