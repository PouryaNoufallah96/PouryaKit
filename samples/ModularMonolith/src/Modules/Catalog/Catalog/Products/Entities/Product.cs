using Catalog.Categories.ValueObjects;
using Catalog.Products.Errors;
using Catalog.Products.Events.DomainEvents;
using Catalog.Products.ValueObjects;
using PouryaKit.Domain;

namespace Catalog.Products.Entities;

internal sealed class Product : AggregateRoot<ProductId>
{
    private Product() { }

    public string Name { get; private set; } = "";

    public CategoryId CategoryId { get; private set; } = null!;

    public static Result<Product> Create(ProductId id, CategoryId categoryId, string name)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(categoryId);
        if (string.IsNullOrWhiteSpace(name))
        {
            return ProductErrors.NameRequired;
        }

        var product = new Product
        {
            Id = id,
            CategoryId = categoryId,
            Name = name.Trim(),
        };
        product.Raise(new ProductCreatedDomainEvent(id, categoryId, product.Name));
        return product;
    }
}
