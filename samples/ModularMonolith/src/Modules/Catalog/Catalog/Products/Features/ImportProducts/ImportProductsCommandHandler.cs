using Catalog.Products.Data.Repository;
using Catalog.Products.Entities;
using Catalog.Products.ValueObjects;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace Catalog.Products.Features.ImportProducts;

internal sealed class ImportProductsCommandHandler(IProductRepository products)
    : CommandHandler<ImportProductsCommand, int>
{
    public override async Task<Result<int>> Handle(ImportProductsCommand command, CancellationToken cancellationToken)
    {
        // Names are compared as Product stores them. A name already in use is skipped, which makes a repeated import
        // harmless; it is not a uniqueness check.
        var names = command.Names.Select(name => name.Trim()).Distinct().ToList();
        var inUse = await products.ListNamesInUseAsync(names, cancellationToken);

        var imported = 0;
        foreach (var name in names.Except(inUse))
        {
            var product = Product.Create(new ProductId(Guid.CreateVersion7()), name);
            if (product.IsFailure)
            {
                return product.Errors;
            }

            await products.AddAsync(product.Value, cancellationToken);
            imported++;
        }

        return imported;
    }
}
