namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// Thrown by a delivery that arrived on the queue of a subscription this Host does not declare, typically one renamed
/// or removed while deliveries to it were stored. The delivery is never treated as handled.
/// </summary>
public sealed class UnknownSubscriptionException(string subscription)
    : Exception(
        $"No Notification Handler or Integration Event Handler subscribes under the name {subscription} in this Host, so its delivery cannot run. Declare the subscription again, under that name, to deliver it."
    )
{
    /// <summary>The subscription name the delivery arrived for.</summary>
    public string Subscription { get; } = subscription;
}
