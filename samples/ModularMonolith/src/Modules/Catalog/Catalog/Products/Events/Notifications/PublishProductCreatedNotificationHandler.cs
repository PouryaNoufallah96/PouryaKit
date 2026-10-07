using Catalog.Contracts.Products.Events.V1;
using Catalog.Contracts.Products.Events.V2;
using Catalog.Products.Events.DomainEvents;
using PouryaKit.Domain;
using PouryaKit.Messaging;

namespace Catalog.Products.Events.Notifications;

// Tells other Modules about the new product once it is committed. The Integration Events carry the Domain Event's
// identity and time, so every receiver recognises a redelivery. Both versions are published while subscriptions move
// from v1 to v2; retiring v1 is removing it here and its Publish declaration once no Module subscribes to it.
internal sealed class PublishProductCreatedNotificationHandler(IIntegrationEventPublisher events)
    : INotificationHandler<ProductCreatedDomainEvent>
{
    public Task<Result> Handle(ProductCreatedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        events.Publish(
            domainEvent.Id,
            new ProductCreatedV1(domainEvent.Id, domainEvent.OccurredOn, domainEvent.ProductId.Value, domainEvent.Name)
        );
        events.Publish(
            domainEvent.Id,
            new ProductCreatedV2(
                domainEvent.Id,
                domainEvent.OccurredOn,
                domainEvent.ProductId.Value,
                domainEvent.Name,
                domainEvent.CategoryId.Value
            )
        );
        return Task.FromResult(Result.Success());
    }
}
