using Catalog.Products.Data.Specifications;
using Catalog.Products.Entities;
using Catalog.Products.ValueObjects;
using PouryaKit.Cqrs;
using PouryaKit.Persistence.EFCore;

namespace Catalog.Products.Data.Repository;

// The generic repository answers with aggregates, a flag or a count. Importing needs only the names already in use,
// a projection it cannot express, so this Business Area adds one member and inherits the rest.
internal sealed class ProductRepository(IUnitOfWork unitOfWork)
    : EfRepository<Product, ProductId>(unitOfWork),
        IProductRepository
{
    public async Task<List<string>> ListNamesInUseAsync(
        IReadOnlyCollection<string> names,
        CancellationToken cancellationToken = default
    ) => [.. (await ListAsync(new ProductNamesInUse(names), cancellationToken)).Distinct()];
}
