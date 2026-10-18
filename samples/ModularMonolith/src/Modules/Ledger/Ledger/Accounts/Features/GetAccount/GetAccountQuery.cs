using PouryaKit.Cqrs;

namespace Ledger.Accounts.Features.GetAccount;

internal sealed record GetAccountQuery(Guid AccountId) : Query<AccountDto>;
