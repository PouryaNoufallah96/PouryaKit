using S2.Kit;
using S2.Kit.Harness;

namespace S2.Catalog;

/// <summary>
/// Catalog's Notifications, in the shape plan section D and the glossary give them: the Domain Event is internal to
/// Catalog, and so are its two Notification Handlers. Plan section D declares neither a wire name for the Domain
/// Event nor a subscription name for a Notification Handler; the Spike declares both here.
/// </summary>
public static class CatalogNotifications
{
    public const string WireName = "catalog.product-created";
    public const string IndexSubscription = "catalog.product-created.index";
    public const string AnnounceSubscription = "catalog.product-created.announce";
    public const string IndexHandler = nameof(IndexProductOnProductCreated);
    public const string AnnounceHandler = nameof(AnnounceProductOnProductCreated);

    public static void Configure(KitMessaging messaging)
    {
        messaging.Publish<ProductCreated>(WireName);
        messaging.SubscribeNotification<ProductCreated, IndexProductOnProductCreated>(IndexSubscription);
        messaging.SubscribeNotification<ProductCreated, AnnounceProductOnProductCreated>(AnnounceSubscription);
    }

    /// <summary>Stand-in Operation: adds a product and raises the Domain Event, which the Unit of Work stores as a Notification.</summary>
    public static Task RaiseProductCreatedAsync(UnitOfWork<CatalogDbContext> uow, Guid productId) =>
        uow.ExecuteAsync((db, outgoing) =>
        {
            db.Products.Add(new Product { Id = productId, Name = "notified" });
            outgoing.Add(new ProductCreated(Guid.CreateVersion7(), productId));
            return Task.CompletedTask;
        });
}

internal sealed record ProductCreated(Guid EventId, Guid ProductId);

internal sealed class IndexProductOnProductCreated(DeliveryLog log) : INotificationHandler<ProductCreated>
{
    public Task<Result> Handle(ProductCreated domainEvent, CancellationToken ct) =>
        log.RunAsync(CatalogNotifications.IndexHandler, domainEvent.ProductId, domainEvent.EventId);
}

internal sealed class AnnounceProductOnProductCreated(DeliveryLog log) : INotificationHandler<ProductCreated>
{
    public Task<Result> Handle(ProductCreated domainEvent, CancellationToken ct) =>
        log.RunAsync(CatalogNotifications.AnnounceHandler, domainEvent.ProductId, domainEvent.EventId);
}
