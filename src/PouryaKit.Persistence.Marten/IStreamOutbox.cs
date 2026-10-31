using Marten;

namespace PouryaKit.Persistence.Marten;

/// <summary>
/// The Outbox step of the Marten Unit of Work's commit: what an Operation publishes is written into the message store
/// through the Unit of Work's session, so it commits with the Stream's events or not at all, and is sent after the commit.
/// A messaging package registers it, scoped; the Marten Unit of Work calls it when one is registered. Feature code never
/// does.
/// </summary>
public interface IStreamOutbox
{
    /// <summary>
    /// Writes each of <paramref name="messages"/> that has a subscription, the Domain Events the Operation handled in the
    /// order handled, through <paramref name="session"/>, which is open over the Unit of Work's transaction, once per
    /// commit, after the Streams' events are appended and before the save; the save then commits and sends them.
    /// </summary>
    Task WriteAsync(
        IDocumentSession session,
        IReadOnlyList<object> messages,
        CancellationToken cancellationToken
    );
}
