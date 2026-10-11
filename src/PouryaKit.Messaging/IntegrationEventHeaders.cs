namespace PouryaKit.Messaging;

/// <summary>
/// The message header that carries an Integration Event's identity, which is the identity of the Domain Event it follows
/// from. Every delivery of it carries the same value, so the receiving handler's Receipt recognises a redelivery. Every
/// transport writes and reads this name.
/// </summary>
public static class IntegrationEventHeaders
{
    /// <summary>The identity of the Domain Event the Integration Event follows from.</summary>
    public const string DomainEventId = "pouryakit-domain-event-id";
}
