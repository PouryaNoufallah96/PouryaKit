using Catalog.Products.Entities;
using Catalog.Products.ValueObjects;
using PouryaKit.Persistence;

namespace Catalog.Products.Data.Repository;

internal interface IProductRepository : IRepository<Product, ProductId>
{
    /// <summary>Which of <paramref name="names"/> a product already has, without loading or tracking any product.</summary>
    Task<List<string>> ListNamesInUseAsync(
        IReadOnlyCollection<string> names,
        CancellationToken cancellationToken = default
    );
}
