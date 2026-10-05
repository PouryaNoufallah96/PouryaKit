using FluentValidation;

namespace Catalog.Products.Features.RemoveProducts;

internal sealed class RemoveProductsCommandValidator : AbstractValidator<RemoveProductsCommand>
{
    public RemoveProductsCommandValidator()
    {
        RuleFor(command => command.Names).NotEmpty();
        RuleForEach(command => command.Names).NotEmpty();
    }
}
