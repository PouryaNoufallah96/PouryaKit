using FluentValidation;

namespace Ledger.Accounts.Features.OpenAccount;

internal sealed class OpenAccountCommandValidator : AbstractValidator<OpenAccountCommand>
{
    public OpenAccountCommandValidator()
    {
        RuleFor(command => command.AccountId).NotEmpty();
        RuleFor(command => command.Holder).NotEmpty();
    }
}
