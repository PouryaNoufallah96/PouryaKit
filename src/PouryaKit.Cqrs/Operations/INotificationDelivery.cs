using PouryaKit.Domain;

namespace PouryaKit.Cqrs;

/// <summary>
/// The Notification the delivery running in this scope delivers to one Notification Handler, which the Kit's delivery
/// step records in that delivery's own scope. Every other scope records none, one that a Gateway call opens from the
/// delivery included, whatever <see cref="OperationContext"/> it is given: a context can be passed on, its scope cannot.
/// A Kit package reads it to allow what only a Notification Handler's delivery may do, as the Integration Event
/// publisher and the realtime publisher do; only the delivery step implements and sets it, and Feature code never
/// reads it.
/// </summary>
public interface INotificationDelivery
{
    /// <summary>The Domain Event delivered; null outside a Notification Handler's delivery.</summary>
    DomainEvent? Notification { get; }
}
