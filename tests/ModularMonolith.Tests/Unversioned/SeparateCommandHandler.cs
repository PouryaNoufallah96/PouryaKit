using ModularMonolith.Tests.Unowned;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace ModularMonolith.Tests.Unversioned;

/// <summary>
/// The one command handler of its assembly, for a command of another assembly, as a Contracts assembly holds a Feature's
/// commands: a Host that scans this assembly handles <see cref="UnownedCommand"/> without scanning the command's.
/// </summary>
public sealed class SeparateCommandHandler : CommandHandler<UnownedCommand>
{
    public override Task<Result> Handle(UnownedCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}
