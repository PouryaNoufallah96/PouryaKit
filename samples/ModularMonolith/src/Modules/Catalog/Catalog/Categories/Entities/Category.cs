using Catalog.Categories.Errors;
using Catalog.Categories.ValueObjects;
using PouryaKit.Domain;

namespace Catalog.Categories.Entities;

// Tenant-scoped, as the products it counts are.
internal sealed class Category : AggregateRoot<CategoryId>, ITenantScoped
{
    private Category() { }

    public string Tenant { get; private set; } = "";

    public string Name { get; private set; } = "";

    public int ProductCount { get; private set; }

    // Raises no Domain Event: creating a category is the proof that a command which raises none is still saved.
    public static Result<Category> Create(CategoryId id, string name, string tenant)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        if (string.IsNullOrWhiteSpace(name))
        {
            return CategoryErrors.NameRequired;
        }

        return new Category
        {
            Id = id,
            Tenant = tenant,
            Name = name.Trim(),
        };
    }

    public void CountProduct() => ProductCount++;

    public void UncountProduct() => ProductCount--;
}
