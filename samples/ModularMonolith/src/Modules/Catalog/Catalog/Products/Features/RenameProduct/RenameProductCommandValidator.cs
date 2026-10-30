using FluentValidation;

namespace Catalog.Products.Features.RenameProduct;

#region rename-product-validator
internal sealed class RenameProductCommandValidator : AbstractValidator<RenameProductCommand>
{
    public RenameProductCommandValidator()
    {
        RuleFor(command => command.ProductId).NotEmpty();
        RuleFor(command => command.Name).NotEmpty().MaximumLength(200);
    }
}
#endregion
