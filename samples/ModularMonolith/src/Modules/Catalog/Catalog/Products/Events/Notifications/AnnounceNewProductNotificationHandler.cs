using Catalog.Contracts.Products.Realtime;
using Catalog.Products.Events.DomainEvents;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Realtime;

namespace Catalog.Products.Events.Notifications;

#region announce-new-product-handler
// Runs after the product is committed, never inside its transaction: an announcement of a product that was then rolled
// back could not be taken back. The browsers of the product's tenant get it when this delivery commits.
internal sealed class AnnounceNewProductNotificationHandler(
    INewProductAnnouncer announcer,
    IRealtimePublisher realtime
) : INotificationHandler<ProductCreatedDomainEvent>
{
    public async Task<Result> Handle(
        ProductCreatedDomainEvent domainEvent,
        CancellationToken cancellationToken
    )
    {
        await announcer.AnnounceAsync(
            new NewProductAnnouncement(domainEvent.ProductId.Value, domainEvent.Name),
            cancellationToken
        );
        return await realtime.PublishAsync(
            NewProduct.Channel,
            new NewProduct(domainEvent.ProductId.Value, domainEvent.Name),
            cancellationToken
        );
    }
}
#endregion
