using FluentValidation;

namespace Catalog.Products.Features.AskAboutProducts;

internal sealed class AskAboutProductsQueryValidator : AbstractValidator<AskAboutProductsQuery>
{
    public AskAboutProductsQueryValidator() => RuleFor(query => query.Question).NotEmpty().MaximumLength(2000);
}
