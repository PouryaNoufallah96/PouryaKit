using FluentValidation;

namespace Catalog.Products.Features.AskAboutProducts;

internal sealed class AskAboutProductsCommandValidator : AbstractValidator<AskAboutProductsCommand>
{
    public AskAboutProductsCommandValidator() =>
        RuleFor(command => command.Question).NotEmpty().MaximumLength(2000);
}
