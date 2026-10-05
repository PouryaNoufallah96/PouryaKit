using Catalog.Categories.ValueObjects;
using Catalog.Products.ValueObjects;
using PouryaKit.Domain;

namespace Catalog.Products.Events.DomainEvents;

internal sealed record ProductCreatedDomainEvent(ProductId ProductId, CategoryId CategoryId, string Name) : DomainEvent;
