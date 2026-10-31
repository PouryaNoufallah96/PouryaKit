using Catalog.Categories.ValueObjects;
using Catalog.Products.Data.Repository;
using Catalog.Products.Entities;
using Catalog.Products.Errors;
using Catalog.Products.ValueObjects;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace Catalog.Products.Features.ImportProducts;

internal sealed class ImportProductsCommandHandler(
    IProductRepository products,
    OperationContext context
) : CommandHandler<ImportProductsCommand, int>
{
    public override async Task<Result<int>> Handle(
        ImportProductsCommand command,
        CancellationToken cancellationToken
    )
    {
        // Names are compared as Product stores them. A name given twice is a mistake in the request, refused before
        // anything else. A name already in use is skipped, which makes a repeated import harmless. That is a courtesy,
        // not the guard: the unique index on the name is, so a name a concurrent Operation takes first fails the import
        // with ProductErrors.NameInUse. Names are a tenant's own: another tenant's products are neither read nor in use.
        var names = command.Names.Select(name => name.Trim()).ToList();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
        {
            return ProductErrors.NameRepeated;
        }

        var inUse = await products.ListNamesInUseAsync(names, cancellationToken);

        var categoryId = new CategoryId(command.CategoryId);
        var imported = 0;
        foreach (var name in names.Where(candidate => !inUse.Contains(candidate)))
        {
            // Catalog is tenant-scoped: an Operation without a tenant never reaches its handlers.
            var product = Product.Create(
                new ProductId(Guid.CreateVersion7()),
                categoryId,
                name,
                context.Tenant!
            );
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
