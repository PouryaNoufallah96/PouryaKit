using Catalog.Categories.ValueObjects;
using Catalog.Products.ValueObjects;

namespace Catalog.Products.Features.ListProducts;

internal sealed record ProductListItemDto(ProductId Id, CategoryId CategoryId, string Name);
