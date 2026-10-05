using Ardalis.Specification;
using Catalog.Products.Entities;

namespace Catalog.Products.Data.Specifications;

internal sealed class ProductNamesInUse : Specification<Product, string>
{
    public ProductNamesInUse(IReadOnlyCollection<string> names)
    {
        Query.Where(product => names.Contains(product.Name));
        Query.Select(product => product.Name);
    }
}
