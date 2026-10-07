using PouryaKit.Cqrs;

namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// Where the commands an Operation schedules wait for its Unit of Work: they are written to the Outbox in its commit,
/// or dropped with it. A persistence package registers it, scoped, with the Outbox that writes them; a Host without one
/// cannot schedule commands. Feature code schedules through <see cref="IDelayedCommands"/> and never calls it.
/// </summary>
public interface IDelayedCommandOutbox
{
    /// <summary>
    /// Adds <paramref name="command"/>, due at <paramref name="at"/>, to what the active Unit of Work writes to the
    /// Outbox when it commits.
    /// </summary>
    void Add(Command command, DateTimeOffset at);
}
