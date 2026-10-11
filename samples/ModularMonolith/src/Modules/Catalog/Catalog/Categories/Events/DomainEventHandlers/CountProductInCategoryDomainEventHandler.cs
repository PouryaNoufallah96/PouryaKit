using Catalog.Categories.Entities;
using Catalog.Categories.Errors;
using Catalog.Categories.ValueObjects;
using Catalog.Products.Events.DomainEvents;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

namespace Catalog.Categories.Events.DomainEventHandlers;

// Counts a new product in its category in the same transaction as the product: both are saved, or neither is.
internal sealed class CountProductInCategoryDomainEventHandler(IRepository<Category, CategoryId> categories)
    : IDomainEventHandler<ProductCreatedDomainEvent>
{
    public async Task<Result> Handle(ProductCreatedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        var category = await categories.FindAsync(domainEvent.CategoryId, cancellationToken);
        if (category is null)
        {
            return CategoryErrors.NotFound;
        }

        category.CountProduct();
        return Result.Success();
    }
}
