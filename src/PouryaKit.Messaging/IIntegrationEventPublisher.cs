namespace PouryaKit.Messaging;

/// <summary>
/// Publishes Integration Events for other Modules. A Notification Handler injects it and publishes the facts its Domain
/// Event means to other Modules, each a plain record of the publishing Module's Contracts that the Module declared, with
/// a wire name, in its Composition Root. Nothing is sent from here: the Integration Event is written to the Module's
/// Outbox in the commit of the Notification Handler's delivery, together with its Receipt and its effect, once for each
/// subscription, and sent after that commit; a delivery that fails leaves none.
/// </summary>
public interface IIntegrationEventPublisher
{
    /// <summary>
    /// Publishes <paramref name="integrationEvent"/>, which follows from the Domain Event identified by
    /// <paramref name="domainEventId"/>: the one the Notification Handler handles. That identity is the Integration
    /// Event's own, in every delivery and redelivery, so each Integration Event Handler applies it once. One Domain
    /// Event therefore yields at most one Integration Event of each type, from one Notification Handler: a second one
    /// would carry the same identity, and every receiver would acknowledge it without applying it. Throws when called
    /// outside a Notification Handler's delivery, where the Operation only reads (in a query, an authorizer or a
    /// validator), with the identity of another Domain Event, with a type the running Module did not declare it
    /// publishes, or a second time with one type in one delivery. When two Notification Handlers of one Domain Event
    /// publish one type, the second delivery to commit fails, and keeps failing until it is set aside.
    /// </summary>
    void Publish<TIntegrationEvent>(Guid domainEventId, TIntegrationEvent integrationEvent)
        where TIntegrationEvent : class;
}
