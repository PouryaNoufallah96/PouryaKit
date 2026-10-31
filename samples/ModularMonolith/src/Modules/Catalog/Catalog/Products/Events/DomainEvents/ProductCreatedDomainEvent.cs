using Catalog.Categories.ValueObjects;
using Catalog.Products.ValueObjects;
using PouryaKit.Domain;

namespace Catalog.Products.Events.DomainEvents;

#region product-created-domain-event
internal sealed record ProductCreatedDomainEvent(
    ProductId ProductId,
    CategoryId CategoryId,
    string Name
) : DomainEvent;
#endregion
