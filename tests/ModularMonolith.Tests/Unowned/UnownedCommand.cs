using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace ModularMonolith.Tests.Unowned;

/// <summary>A Feature's command whose Host registers no Unit of Work.</summary>
public sealed record UnownedCommand : Command;

internal sealed class UnownedCommandHandler : CommandHandler<UnownedCommand>
{
    public override Task<Result> Handle(UnownedCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}
