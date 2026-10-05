using PouryaKit.Cqrs;

namespace Catalog.Products.Features.RenameProduct;

internal sealed record RenameProductCommand(Guid ProductId, string Name) : Command;
