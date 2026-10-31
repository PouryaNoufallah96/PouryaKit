using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Security.Tests.Contracts;

namespace PouryaKit.Security.Tests.Module;

/// <summary>The commands the test Module's handlers ran in a scope.</summary>
public sealed class HandledCommands
{
    /// <summary>Each command a handler ran, in order.</summary>
    public List<Type> Commands { get; } = [];
}

// Handles a command its Contracts declare, outside this assembly.
internal sealed class RevokeAccessHandler(HandledCommands handled)
    : CommandHandler<RevokeAccessCommand>
{
    public override Task<Result> Handle(
        RevokeAccessCommand command,
        CancellationToken cancellationToken
    )
    {
        handled.Commands.Add(typeof(RevokeAccessCommand));
        return Task.FromResult(Result.Success());
    }
}
