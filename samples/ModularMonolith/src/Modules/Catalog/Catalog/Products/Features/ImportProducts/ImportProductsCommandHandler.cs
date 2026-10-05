using Catalog.Categories.ValueObjects;
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
        // harmless. That is a courtesy, not the guard: the unique index on the name is, so a name given twice, or taken
        // by a concurrent Operation, fails the import with ProductErrors.NameInUse.
        var names = command.Names.Select(name => name.Trim()).ToList();
        var inUse = await products.ListNamesInUseAsync(names, cancellationToken);

        var categoryId = new CategoryId(command.CategoryId);
        var imported = 0;
        foreach (var name in names.Except(inUse))
        {
            var product = Product.Create(new ProductId(Guid.CreateVersion7()), categoryId, name);
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
