using Catalog.Contracts.Products;
using Catalog.Products.Entities;
using Catalog.Products.ValueObjects;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

namespace Catalog.Products.Features.GetProductSummary;

internal sealed class GetProductSummaryQueryHandler(IRepository<Product, ProductId> products)
    : QueryHandler<GetProductSummaryQuery, ProductSummary>
{
    public static readonly Error ProductNotFound = Error.NotFound("Product.NotFound", "There is no product with this id.");

    public override async Task<Result<ProductSummary>> Handle(
        GetProductSummaryQuery query,
        CancellationToken cancellationToken
    )
    {
        var product = await products.FindAsync(new ProductId(query.ProductId), cancellationToken);
        return product is null ? ProductNotFound : new ProductSummary(product.Id.Value, product.Name);
    }
}
