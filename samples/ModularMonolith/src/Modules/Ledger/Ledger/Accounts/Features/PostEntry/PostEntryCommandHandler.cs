using Ledger.Accounts.Entities;
using Ledger.Accounts.Errors;
using Ledger.Accounts.ValueObjects;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence.Marten;

namespace Ledger.Accounts.Features.PostEntry;

#region post-entry-handler
// The entry appends at the version the account was read at: another entry appended meanwhile fails this one with the
// Kit's concurrency conflict, and nothing is appended.
internal sealed class PostEntryCommandHandler(IStreams streams) : CommandHandler<PostEntryCommand>
{
    public override async Task<Result> Handle(PostEntryCommand command, CancellationToken cancellationToken)
    {
        if (await streams.LoadAsync<Account>(new AccountId(command.AccountId), cancellationToken) is not { } account)
        {
            return AccountErrors.NotFound;
        }

        account.Post(command.Amount);
        return Result.Success();
    }
}
#endregion
