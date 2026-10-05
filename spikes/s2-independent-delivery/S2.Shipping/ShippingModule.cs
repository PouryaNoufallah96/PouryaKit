using S2.Catalog.Contracts;
using S2.Kit;
using S2.Kit.Harness;

namespace S2.Shipping;

/// <summary>A third subscriber whose handler keeps the realistic shape: internal to its Module assembly.</summary>
public static class ShippingModule
{
    public const string Subscription = "shipping.Product-Created"; // mixed case: Wolverine lower-cases queue names
    public const string HandlerName = nameof(PrepareShippingOnProductCreated);

    public static void Configure(KitMessaging messaging) =>
        messaging.Subscribe<ProductCreatedV1, PrepareShippingOnProductCreated>(Subscription);
}

internal sealed class PrepareShippingOnProductCreated(DeliveryLog log) : IIntegrationEventHandler<ProductCreatedV1>
{
    public Task<Result> Handle(ProductCreatedV1 message, CancellationToken ct) =>
        log.RunAsync(ShippingModule.HandlerName, message.ProductId, message.EventId);
}
