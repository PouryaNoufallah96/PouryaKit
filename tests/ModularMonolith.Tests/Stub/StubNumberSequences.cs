using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence.Postgres;

namespace ModularMonolith.Tests.Stub;

// Takes a number of each scope key in turn, in the Operation's Module: after the first it reports through the hold and
// waits to be let go, so two of them that take the same keys in opposite orders each hold one row the other needs.
internal sealed record TakeNumbersCommand(string[] ScopeKeys, Hold Hold) : Command;

internal sealed class TakeNumbersCommandHandler(INumberSequences sequences) : CommandHandler<TakeNumbersCommand>
{
    public override async Task<Result> Handle(TakeNumbersCommand command, CancellationToken cancellationToken)
    {
        foreach (var scopeKey in command.ScopeKeys)
        {
            var number = await sequences.NextAsync(scopeKey, cancellationToken);
            if (number.IsFailure)
            {
                return number;
            }

            if (command.Hold.Changed.TrySetResult())
            {
                await command.Hold.Released.Task.WaitAsync(cancellationToken);
            }
        }

        return Result.Success();
    }
}
