using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Messaging.Wolverine;

// Scoped, and set by DeliverHandler alone, in the delivery's own scope, so nothing outside this package can grant what
// it records: Cqrs declares only the reading side.
internal sealed class NotificationDelivery : INotificationDelivery
{
    public DomainEvent? Notification { get; set; }
}
