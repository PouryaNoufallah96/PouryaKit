using Catalog.Categories.ValueObjects;
using Catalog.Products.Errors;
using Catalog.Products.Events.DomainEvents;
using Catalog.Products.ValueObjects;
using PouryaKit.Domain;

namespace Catalog.Products.Entities;

// Auditable and soft-deletable: the Unit of Work sets those properties, so the domain never does. Tenant-scoped: the
// product sets its own tenant, from the Operation that creates it.
internal sealed class Product : AggregateRoot<ProductId>, IAuditable, ISoftDeletable, ITenantScoped
{
    // Set by Remove, not mapped: IsDeleted is set only by the Unit of Work, after every handler of the Operation ran.
    private bool _removed;

    private Product() { }

    public string Tenant { get; private set; } = "";

    public string Name { get; private set; } = "";

    public CategoryId CategoryId { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }

    public string CreatedBy { get; private set; } = "";

    public DateTimeOffset LastChangedAt { get; private set; }

    public string LastChangedBy { get; private set; } = "";

    public bool IsDeleted { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public string? DeletedBy { get; private set; }

    public static Result<Product> Create(ProductId id, CategoryId categoryId, string name, string tenant)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(categoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        if (string.IsNullOrWhiteSpace(name))
        {
            return ProductErrors.NameRequired;
        }

        var product = new Product
        {
            Id = id,
            Tenant = tenant,
            CategoryId = categoryId,
            Name = name.Trim(),
        };
        product.Raise(new ProductCreatedDomainEvent(id, categoryId, product.Name));
        return product;
    }

    // Raises no Domain Event: renaming is the proof that a change with none still conflicts with a concurrent one.
    public Result Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ProductErrors.NameRequired;
        }

        Name = name.Trim();
        return Result.Success();
    }

    /// <summary>
    /// Records that the product is leaving the catalog, once; the repository's <c>Remove</c> then removes it. Removing it
    /// again in the same Operation raises nothing: it is still found until the Unit of Work marks it deleted.
    /// </summary>
    public void Remove()
    {
        if (_removed)
        {
            return;
        }

        _removed = true;
        Raise(new ProductRemovedDomainEvent(Id, CategoryId));
    }
}
