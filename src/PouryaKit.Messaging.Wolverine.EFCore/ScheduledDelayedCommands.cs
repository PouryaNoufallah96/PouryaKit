using PouryaKit.Cqrs;

namespace PouryaKit.Messaging.Wolverine.EFCore;

// The commands scheduled in one scope's Operation, which the Outbox takes when the Unit of Work writes it.
internal sealed class ScheduledDelayedCommands : IDelayedCommandOutbox
{
    private readonly List<(Command Command, DateTimeOffset At)> _scheduled = [];

    public void Add(Command command, DateTimeOffset at) => _scheduled.Add((command, at));

    public List<(Command Command, DateTimeOffset At)> Take()
    {
        List<(Command, DateTimeOffset)> taken = [.. _scheduled];
        _scheduled.Clear();
        return taken;
    }
}
