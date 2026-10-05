using S2.Catalog.Contracts;
using S2.Kit;
using S2.Kit.Harness;

namespace S2.Inventory;

public static class InventoryModule
{
    public const string Subscription = "inventory.product-created";
    public const string HandlerName = nameof(ReserveStockOnProductCreated);

    public static void Configure(KitMessaging messaging, string subscription = Subscription) =>
        messaging.Subscribe<ProductCreatedV1, ReserveStockOnProductCreated>(subscription);
}

/// <summary>A Kit-shaped Integration Event Handler: no Wolverine type, returns a Result.</summary>
public sealed class ReserveStockOnProductCreated(DeliveryLog log) : IIntegrationEventHandler<ProductCreatedV1>
{
    public Task<Result> Handle(ProductCreatedV1 message, CancellationToken ct) =>
        log.RunAsync(InventoryModule.HandlerName, message.ProductId, message.EventId);
}
