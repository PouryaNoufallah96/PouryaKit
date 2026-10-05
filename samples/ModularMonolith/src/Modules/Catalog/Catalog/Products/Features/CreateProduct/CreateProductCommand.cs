using PouryaKit.Cqrs;

namespace Catalog.Products.Features.CreateProduct;

internal sealed record CreateProductCommand(Guid ProductId, string Name) : Command<Guid>;
