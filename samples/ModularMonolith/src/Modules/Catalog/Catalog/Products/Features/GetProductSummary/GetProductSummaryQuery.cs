using Catalog.Contracts.Products;
using PouryaKit.Cqrs;

namespace Catalog.Products.Features.GetProductSummary;

internal sealed record GetProductSummaryQuery(Guid ProductId) : Query<ProductSummary>;
