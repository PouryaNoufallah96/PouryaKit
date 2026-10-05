using FluentValidation;

namespace Catalog.Products.Features.ImportProducts;

internal sealed class ImportProductsCommandValidator : AbstractValidator<ImportProductsCommand>
{
    public ImportProductsCommandValidator()
    {
        RuleFor(command => command.CategoryId).NotEmpty();
        RuleFor(command => command.Names).NotEmpty();
        // A blank name is left to Product.Create, so one bad name fails the whole import.
        RuleForEach(command => command.Names).NotNull().MaximumLength(200);
    }
}
