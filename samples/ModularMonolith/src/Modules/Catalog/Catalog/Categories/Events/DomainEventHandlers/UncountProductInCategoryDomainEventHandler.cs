using Catalog.Categories.Entities;
using Catalog.Categories.Errors;
using Catalog.Categories.ValueObjects;
using Catalog.Products.Events.DomainEvents;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

namespace Catalog.Categories.Events.DomainEventHandlers;

// Lowers the count in the same transaction as the product's removal: both are saved, or neither is.
internal sealed class UncountProductInCategoryDomainEventHandler(IRepository<Category, CategoryId> categories)
    : IDomainEventHandler<ProductRemovedDomainEvent>
{
    public async Task<Result> Handle(ProductRemovedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        var category = await categories.FindAsync(domainEvent.CategoryId, cancellationToken);
        if (category is null)
        {
            return CategoryErrors.NotFound;
        }

        category.UncountProduct();
        return Result.Success();
    }
}
