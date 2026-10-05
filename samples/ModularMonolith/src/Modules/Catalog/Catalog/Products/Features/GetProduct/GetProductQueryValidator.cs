using FluentValidation;

namespace Catalog.Products.Features.GetProduct;

internal sealed class GetProductQueryValidator : AbstractValidator<GetProductQuery>
{
    public GetProductQueryValidator() => RuleFor(query => query.ProductId).NotEmpty();
}
