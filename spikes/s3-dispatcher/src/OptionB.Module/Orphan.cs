using Mediator;
using Spike.Common;

namespace OptionB.Module;

// A command with no handler, to observe where Mediator reports it.
internal sealed record OrphanCommand : ICommand<Result<string>>;

public static class OrphanGateway
{
    public static ValueTask<Result<string>> SendOrphan(ISender sender) => sender.Send(new OrphanCommand());
}
