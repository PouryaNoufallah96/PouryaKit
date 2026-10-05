using Catalog.Products.Errors;
using Catalog.Products.ValueObjects;
using PouryaKit.Domain;

namespace Catalog.Products.Entities;

internal sealed class Product : AggregateRoot<ProductId>
{
    private Product() { }

    public string Name { get; private set; } = "";

    // Raises no Domain Event: nothing handles one before ticket #10, and creating a product is the proof that a
    // command which raises none is still saved.
    public static Result<Product> Create(ProductId id, string name)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (string.IsNullOrWhiteSpace(name))
        {
            return ProductErrors.NameRequired;
        }

        return new Product { Id = id, Name = name.Trim() };
    }
}
