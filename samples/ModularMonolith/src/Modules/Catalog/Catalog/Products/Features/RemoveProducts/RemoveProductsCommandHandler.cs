using Catalog.Products.Data.Specifications;
using Catalog.Products.Entities;
using Catalog.Products.ValueObjects;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

namespace Catalog.Products.Features.RemoveProducts;

internal sealed class RemoveProductsCommandHandler(IRepository<Product, ProductId> products)
    : CommandHandler<RemoveProductsCommand, int>
{
    public override async Task<Result<int>> Handle(
        RemoveProductsCommand command,
        CancellationToken cancellationToken
    )
    {
        var named = await products.ListAsync(new ProductsNamed(command.Names), cancellationToken);
        foreach (var product in named)
        {
            product.Remove();
            products.Remove(product);
        }

        return named.Count;
    }
}
