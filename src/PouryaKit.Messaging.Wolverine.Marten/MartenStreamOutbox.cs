using Marten;
using PouryaKit.Persistence.Marten;
using Wolverine.Marten;
using Wolverine.Runtime;

namespace PouryaKit.Messaging.Wolverine.Marten;

// Writes what a Stream Module's Operation published through the store-neutral OutboxWriter into a MartenOutbox enrolled in
// the Unit of Work's session (ADR 0011): its envelopes are stored in the message store's tables by the session's save, in
// the transaction of the Streams' events, and sent by the listener the outbox adds to the session, after Marten's commit.
// A Stream has no Snapshots: what it publishes are the Notifications and Integration Events of its events.
internal sealed class MartenStreamOutbox(IWolverineRuntime runtime, OutboxWriter writer) : IStreamOutbox, IOutbox
{
    public Task WriteAsync(IDocumentSession session, IReadOnlyList<object> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(messages);
        return writer.WriteAsync(() => new MartenOutbox(runtime, session), messages, [], cancellationToken);
    }
}
