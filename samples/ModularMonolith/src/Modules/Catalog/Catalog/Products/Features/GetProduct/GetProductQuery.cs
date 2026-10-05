using PouryaKit.Cqrs;

namespace Catalog.Products.Features.GetProduct;

internal sealed record GetProductQuery(Guid ProductId) : Query<ProductDto>;
