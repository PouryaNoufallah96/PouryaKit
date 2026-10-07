namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// Thrown by a delayed command's delivery that arrived on a queue this Host declares for no delayed command of its type,
/// typically one stored before its declaration was renamed or removed, or for one of the other kind: an occurrence of a
/// recurring command where the name is now declared for a delayed command, or a delayed command where it is now declared
/// recurring. The delivery is retried, then set aside as a dead letter, and never runs under another name's Receipts or
/// the other kind's authority.
/// </summary>
public sealed class UnknownDelayedCommandException : Exception
{
    public UnknownDelayedCommandException(string name, Type command)
        : base(
            $"No delayed command of type {command.FullName} is declared under the name {name} in this Host, so its delivery cannot run. Declare it again, under that name, to run it."
        ) => Name = name;

    internal UnknownDelayedCommandException(string name, Type command, bool storedAsOccurrence)
        : base(
            storedAsOccurrence
                ? $"The delivery of {command.FullName} under the name {name} was stored as an occurrence of a recurring command, but this Host declares {name} as a delayed command, so it is never run on a continued Operation's authority."
                : $"The delivery of {command.FullName} under the name {name} was stored as a delayed command, but this Host declares {name} as a recurring command, so it is never run as that command's system Actor."
        ) => Name = name;

    public string Name { get; }
}
