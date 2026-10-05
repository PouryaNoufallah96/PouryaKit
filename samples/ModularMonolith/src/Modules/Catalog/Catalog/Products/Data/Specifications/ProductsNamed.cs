using Ardalis.Specification;
using Catalog.Products.Entities;

namespace Catalog.Products.Data.Specifications;

internal sealed class ProductsNamed : Specification<Product>
{
    public ProductsNamed(IReadOnlyCollection<string> names) => Query.Where(product => names.Contains(product.Name));
}
