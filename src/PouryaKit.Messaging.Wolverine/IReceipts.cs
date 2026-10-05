namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// Where a handler's Receipts are kept: the record that it has applied one message. The delivery step records the
/// Receipt in the receiving Module's Unit of Work, before the handler runs, so the Receipt commits with the handler's
/// effect or not at all. A persistence package registers it, scoped; without one, every delivery runs its handler.
/// </summary>
public interface IReceipts
{
    /// <summary>
    /// Records that <paramref name="subscription"/> applies the message identified by <paramref name="messageId"/>, in
    /// the active Unit of Work's transaction. Returns false, recording nothing, when a committed Receipt says it already
    /// did. A concurrent delivery of the same message waits until the first one commits or rolls back.
    /// </summary>
    Task<bool> RecordAsync(string subscription, Guid messageId, CancellationToken cancellationToken);
}
