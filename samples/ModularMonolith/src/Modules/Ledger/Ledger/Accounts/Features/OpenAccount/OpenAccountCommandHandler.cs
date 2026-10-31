using Ledger.Accounts.Entities;
using Ledger.Accounts.ValueObjects;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence.Marten;

namespace Ledger.Accounts.Features.OpenAccount;

internal sealed class OpenAccountCommandHandler(IStreams streams)
    : CommandHandler<OpenAccountCommand, Guid>
{
    public override Task<Result<Guid>> Handle(
        OpenAccountCommand command,
        CancellationToken cancellationToken
    )
    {
        streams.Start(Account.Open(new AccountId(command.AccountId), command.Holder));
        return Task.FromResult<Result<Guid>>(command.AccountId);
    }
}
