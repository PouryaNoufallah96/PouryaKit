using FluentValidation;
using FluentValidation.Results;

namespace Catalog.Products.Features.ListProducts;

internal sealed class ListProductsQueryValidator : AbstractValidator<ListProductsQuery>
{
    public ListProductsQueryValidator() =>
        RuleFor(query => query.Page)
            .Cascade(CascadeMode.Stop)
            .NotNull()
            .Custom(
                (page, context) =>
                {
                    foreach (var error in ListProductsQuery.Paging.Validate(page).Errors)
                    {
                        context.AddFailure(new ValidationFailure(error.Target, error.Message) { ErrorCode = error.Code });
                    }
                }
            );
}
