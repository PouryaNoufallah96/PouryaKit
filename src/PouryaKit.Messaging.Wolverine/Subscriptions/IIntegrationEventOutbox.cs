namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// Where the Integration Events an Operation publishes wait for its Unit of Work: they are written to the Outbox in its
/// commit, or dropped with it. <c>UsePouryaKitMessageStore</c> registers it, scoped, for the store package whose Outbox
/// writes them; a Host without one cannot publish Integration Events. Feature code publishes through
/// <see cref="IIntegrationEventPublisher"/> and never calls it.
/// </summary>
public interface IIntegrationEventOutbox
{
    /// <summary>
    /// Adds <paramref name="integrationEvent"/>, identified by <paramref name="domainEventId"/>, to what the active Unit
    /// of Work writes to the Outbox when it commits.
    /// </summary>
    void Add(Guid domainEventId, object integrationEvent);
}
