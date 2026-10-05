using S2.Catalog.Contracts;
using S2.Kit;
using S2.Kit.Harness;

namespace S2.Billing;

public static class BillingModule
{
    public const string Subscription = "billing.product-created";
    public const string HandlerName = nameof(OpenPriceListOnProductCreated);

    public static void Configure(KitMessaging messaging) =>
        messaging.Subscribe<ProductCreatedV1, OpenPriceListOnProductCreated>(Subscription);
}

/// <summary>A Kit-shaped Integration Event Handler: no Wolverine type, returns a Result.</summary>
public sealed class OpenPriceListOnProductCreated(DeliveryLog log) : IIntegrationEventHandler<ProductCreatedV1>
{
    public Task<Result> Handle(ProductCreatedV1 message, CancellationToken ct) =>
        log.RunAsync(BillingModule.HandlerName, message.ProductId, message.EventId);
}
