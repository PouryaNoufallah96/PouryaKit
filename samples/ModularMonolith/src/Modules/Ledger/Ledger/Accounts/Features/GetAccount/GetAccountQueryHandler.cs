using Ledger.Accounts.Entities;
using Ledger.Accounts.Errors;
using Ledger.Accounts.ValueObjects;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence.Marten;

namespace Ledger.Accounts.Features.GetAccount;

// The projection: the account's current state, folded from its stream of the Operation's tenant.
internal sealed class GetAccountQueryHandler(IStreams streams) : QueryHandler<GetAccountQuery, AccountDto>
{
    public override async Task<Result<AccountDto>> Handle(GetAccountQuery query, CancellationToken cancellationToken) =>
        await streams.LoadAsync<Account>(new AccountId(query.AccountId), cancellationToken) is { } account
            ? new AccountDto(account.Id, account.Holder, account.Balance, account.IsActive, account.Version)
            : AccountErrors.NotFound;
}
