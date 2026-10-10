namespace PouryaKit.Messaging;

/// <summary>
/// Gives a message, such as a Domain Event delivered as a Notification, a stable wire name instead of the one
/// <see cref="MessageNames.WireNameOf"/> derives from its type. Stored deliveries carry the wire name, so set it before
/// renaming a type whose deliveries may be in flight, to the name it had.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class WireNameAttribute(string name) : Attribute
{
    /// <summary>The stable wire name the message is stored under.</summary>
    public string Name { get; } = name;
}
