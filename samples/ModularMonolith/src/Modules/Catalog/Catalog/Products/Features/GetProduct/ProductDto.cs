namespace Catalog.Products.Features.GetProduct;

internal sealed record ProductDto(Guid Id, Guid CategoryId, string Name);
