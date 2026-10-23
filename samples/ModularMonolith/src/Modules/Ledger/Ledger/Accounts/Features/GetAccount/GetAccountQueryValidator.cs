using FluentValidation;

namespace Ledger.Accounts.Features.GetAccount;

internal sealed class GetAccountQueryValidator : AbstractValidator<GetAccountQuery>
{
    public GetAccountQueryValidator() => RuleFor(query => query.AccountId).NotEmpty();
}
