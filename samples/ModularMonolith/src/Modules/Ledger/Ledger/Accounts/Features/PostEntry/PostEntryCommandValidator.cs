using FluentValidation;

namespace Ledger.Accounts.Features.PostEntry;

internal sealed class PostEntryCommandValidator : AbstractValidator<PostEntryCommand>
{
    public PostEntryCommandValidator()
    {
        RuleFor(command => command.AccountId).NotEmpty();
        RuleFor(command => command.Amount).NotEqual(0m);
    }
}
