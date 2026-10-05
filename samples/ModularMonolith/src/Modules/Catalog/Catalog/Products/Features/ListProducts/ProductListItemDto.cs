using Catalog.Products.ValueObjects;

namespace Catalog.Products.Features.ListProducts;

internal sealed record ProductListItemDto(ProductId Id, string Name);
