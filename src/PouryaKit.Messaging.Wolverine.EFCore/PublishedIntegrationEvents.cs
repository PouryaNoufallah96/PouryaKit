namespace PouryaKit.Messaging.Wolverine.EFCore;

// The Integration Events published in one scope's Operation, which the Outbox takes when the Unit of Work writes it.
internal sealed class PublishedIntegrationEvents : IIntegrationEventOutbox
{
    private readonly List<(Guid DomainEventId, object IntegrationEvent)> _published = [];

    public void Add(Guid domainEventId, object integrationEvent) => _published.Add((domainEventId, integrationEvent));

    public List<(Guid DomainEventId, object IntegrationEvent)> Take()
    {
        List<(Guid, object)> taken = [.. _published];
        _published.Clear();
        return taken;
    }
}
