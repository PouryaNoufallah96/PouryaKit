namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// Where a handler's Receipts are kept: the record that it has applied one message, and for a Snapshot, the newest
/// version of its aggregate. The delivery step records the Receipt in the receiving Module's Unit of Work, before the
/// handler runs, so the Receipt commits with the handler's effect or not at all. A persistence package registers it,
/// scoped; without one, every delivery runs its handler, and a Snapshot cannot be delivered.
/// </summary>
public interface IReceipts
{
    /// <summary>
    /// Records that <paramref name="subscription"/> applies the message identified by <paramref name="messageId"/>, in
    /// the active Unit of Work's transaction. Returns false, recording nothing, when a committed Receipt says it already
    /// did. A concurrent delivery of the same message waits until the first one commits or rolls back.
    /// </summary>
    Task<bool> RecordAsync(string subscription, Guid messageId, CancellationToken cancellationToken);

    /// <summary>
    /// Records that <paramref name="subscription"/> applies version <paramref name="version"/> of the aggregate
    /// <paramref name="aggregateId"/>, in the active Unit of Work's transaction, only when it is newer than every
    /// version of that aggregate the subscription applied before; returns false, recording nothing, otherwise. A
    /// concurrent delivery of any version of the same aggregate waits until this one commits or rolls back, then
    /// compares with what committed, so of two concurrent versions only the newer is applied.
    /// </summary>
    Task<bool> RecordVersionAsync(string subscription, string aggregateId, long version, CancellationToken cancellationToken);
}
