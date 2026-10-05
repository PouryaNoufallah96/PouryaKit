using PouryaKit.Cqrs;

namespace Catalog.Products.Features.ListProducts;

internal sealed record ListProductsQuery : Query<List<ProductListItemDto>>;
