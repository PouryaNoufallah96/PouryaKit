using Catalog.Products.Entities;
using Catalog.Products.Errors;
using Catalog.Products.ValueObjects;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

namespace Catalog.Products.Features.RenameProduct;

#region rename-product-handler
internal sealed class RenameProductCommandHandler(IRepository<Product, ProductId> products)
    : CommandHandler<RenameProductCommand>
{
    public override async Task<Result> Handle(RenameProductCommand command, CancellationToken cancellationToken)
    {
        var product = await products.FindAsync(new ProductId(command.ProductId), cancellationToken);
        return product is null ? ProductErrors.NotFound : product.Rename(command.Name);
    }
}
#endregion
