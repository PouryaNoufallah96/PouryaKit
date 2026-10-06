using PouryaKit.Cqrs;

namespace Catalog.Products.Features.CreateProduct;

internal sealed record CreateProductCommand(Guid ProductId, Guid CategoryId, string Name) : Command<Guid>;
