using Catalog.Products.Events.DomainEvents;
using PouryaKit.Domain;
using PouryaKit.Messaging;

namespace Catalog.Products.Events.Notifications;

// Runs after the product is committed, never inside its transaction: an announcement of a product that was then rolled
// back could not be taken back.
internal sealed class AnnounceNewProductNotificationHandler(INewProductAnnouncer announcer)
    : INotificationHandler<ProductCreatedDomainEvent>
{
    public async Task<Result> Handle(ProductCreatedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        await announcer.AnnounceAsync(
            new NewProductAnnouncement(domainEvent.ProductId.Value, domainEvent.Name),
            cancellationToken
        );
        return Result.Success();
    }
}
