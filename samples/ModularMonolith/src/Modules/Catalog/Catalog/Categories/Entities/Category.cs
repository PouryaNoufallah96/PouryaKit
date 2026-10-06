using Catalog.Categories.Errors;
using Catalog.Categories.ValueObjects;
using PouryaKit.Domain;

namespace Catalog.Categories.Entities;

internal sealed class Category : AggregateRoot<CategoryId>
{
    private Category() { }

    public string Name { get; private set; } = "";

    public int ProductCount { get; private set; }

    // Raises no Domain Event: creating a category is the proof that a command which raises none is still saved.
    public static Result<Category> Create(CategoryId id, string name)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (string.IsNullOrWhiteSpace(name))
        {
            return CategoryErrors.NameRequired;
        }

        return new Category { Id = id, Name = name.Trim() };
    }

    public void CountProduct() => ProductCount++;

    public void UncountProduct() => ProductCount--;
}
