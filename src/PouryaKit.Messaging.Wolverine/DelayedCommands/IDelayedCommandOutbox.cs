using PouryaKit.Cqrs;

namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// Where the commands an Operation schedules wait for its Unit of Work: they are written to the Outbox in its commit,
/// or dropped with it. <c>UsePouryaKitMessageStore</c> registers it, scoped, for the store package whose Outbox writes
/// them; a Host without one cannot schedule commands. Feature code schedules through <see cref="IDelayedCommands"/> and never calls it.
/// </summary>
public interface IDelayedCommandOutbox
{
    /// <summary>
    /// Adds <paramref name="command"/>, due at <paramref name="at"/>, to what the active Unit of Work writes to the
    /// Outbox when it commits.
    /// </summary>
    void Add(Command command, DateTimeOffset at);

    /// <summary>
    /// Adds <paramref name="command"/>, the occurrence of a recurring command at <paramref name="occurrence"/>, due then,
    /// to what the active Unit of Work writes to the Outbox when it commits, marked with the
    /// <see cref="DelayedCommandHeaders.Occurrence"/> header. The Kit's recurring commands call it; nothing else does.
    /// </summary>
    void AddOccurrence(Command command, DateTimeOffset occurrence);
}
