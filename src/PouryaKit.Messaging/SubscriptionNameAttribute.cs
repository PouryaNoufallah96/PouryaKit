namespace PouryaKit.Messaging;

/// <summary>
/// Gives a Notification Handler a stable subscription name instead of the one
/// <see cref="MessageNames.SubscriptionNameOf"/> derives from its type. The subscription name keys its queue and its
/// Receipts, so set it before renaming a handler whose deliveries may be in flight, to the name it had.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SubscriptionNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
