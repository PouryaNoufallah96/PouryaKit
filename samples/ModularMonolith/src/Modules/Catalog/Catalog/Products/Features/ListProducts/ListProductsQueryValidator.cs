using FluentValidation;
using PouryaKit.Validation;

namespace Catalog.Products.Features.ListProducts;

#region list-products-validator
internal sealed class ListProductsQueryValidator : AbstractValidator<ListProductsQuery>
{
    public ListProductsQueryValidator() =>
        RuleFor(query => query.Page).Cascade(CascadeMode.Stop).NotNull().AllowedBy(ListProductsQuery.Paging);
}
#endregion
