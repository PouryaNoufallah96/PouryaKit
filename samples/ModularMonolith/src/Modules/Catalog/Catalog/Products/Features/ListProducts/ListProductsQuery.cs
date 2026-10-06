using PouryaKit.Cqrs;

namespace Catalog.Products.Features.ListProducts;

internal sealed record ListProductsQuery(PageRequest Page) : Query<Page<ProductListItemDto>>
{
    /// <summary>What a caller may sort and filter the product list by.</summary>
    public static readonly PageDeclaration Paging = new(
        sorts: ["name", "created"],
        filters:
        [
            FilterField.Of<string>("name", FilterOperator.Equal, FilterOperator.Contains),
            FilterField.Of<Guid>("category", FilterOperator.Equal),
        ],
        maxSize: 50
    );
}
