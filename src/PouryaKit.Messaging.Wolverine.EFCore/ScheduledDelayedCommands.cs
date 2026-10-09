using PouryaKit.Cqrs;

namespace PouryaKit.Messaging.Wolverine.EFCore;

// The commands scheduled in one scope's Operation, which the Outbox takes when the Unit of Work writes it.
internal sealed class ScheduledDelayedCommands : IDelayedCommandOutbox
{
    private readonly List<(Command Command, DateTimeOffset At, bool IsOccurrence)> _scheduled = [];

    public void Add(Command command, DateTimeOffset at) => _scheduled.Add((command, at, false));

    public void AddOccurrence(Command command, DateTimeOffset occurrence) => _scheduled.Add((command, occurrence, true));

    public List<(Command Command, DateTimeOffset At, bool IsOccurrence)> Take()
    {
        List<(Command, DateTimeOffset, bool)> taken = [.. _scheduled];
        _scheduled.Clear();
        return taken;
    }
}
