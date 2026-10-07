namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// Thrown by a delayed command's delivery that arrived on a queue this Host declares for no delayed command of its type,
/// typically one stored before its declaration was renamed or removed. The delivery is retried, then set aside as a dead
/// letter, and never runs under another name's Receipts.
/// </summary>
public sealed class UnknownDelayedCommandException(string name, Type command)
    : Exception(
        $"No delayed command of type {command.FullName} is declared under the name {name} in this Host, so its delivery cannot run. Declare it again, under that name, to run it."
    )
{
    public string Name { get; } = name;
}
