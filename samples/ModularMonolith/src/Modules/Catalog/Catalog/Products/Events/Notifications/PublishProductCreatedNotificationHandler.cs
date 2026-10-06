using Catalog.Contracts.Products.Events.V1;
using Catalog.Products.Events.DomainEvents;
using PouryaKit.Domain;
using PouryaKit.Messaging;

namespace Catalog.Products.Events.Notifications;

// Tells other Modules about the new product once it is committed. The Integration Event carries the Domain Event's
// identity and time, so every receiver recognises a redelivery.
internal sealed class PublishProductCreatedNotificationHandler(IIntegrationEventPublisher events)
    : INotificationHandler<ProductCreatedDomainEvent>
{
    public Task<Result> Handle(ProductCreatedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        events.Publish(
            domainEvent.Id,
            new ProductCreatedV1(domainEvent.Id, domainEvent.OccurredOn, domainEvent.ProductId.Value, domainEvent.Name)
        );
        return Task.FromResult(Result.Success());
    }
}
