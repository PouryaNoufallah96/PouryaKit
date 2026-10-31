using Catalog.Categories.ValueObjects;
using Catalog.Products.ValueObjects;
using PouryaKit.Domain;

namespace Catalog.Products.Events.DomainEvents;

internal sealed record ProductRemovedDomainEvent(ProductId ProductId, CategoryId CategoryId)
    : DomainEvent;
