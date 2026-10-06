using Microsoft.Extensions.DependencyInjection;
using Rmq.Contracts;
using Rmq.Kit;

namespace Rmq.Inventory;

/// <summary>
/// Inventory's Composition Root: two subscriptions to the same versioned wire name, published by another Host. One
/// handler always succeeds; the other fails its first <c>auditFailures</c> attempts of each message.
/// </summary>
public static class InventoryModule
{
    public const string ReserveStock = "inventory.reserve-stock";
    public const string AuditProduct = "inventory.audit-product";

    public static void Configure(KitMessaging messaging, IServiceCollection services, int auditFailures)
    {
        messaging.Subscribe<ProductCreatedV1, ReserveStockOnProductCreated>(ReserveStock, ProductCreatedV1.WireName);
        messaging.Subscribe<ProductCreatedV1, AuditProductOnProductCreated>(AuditProduct, ProductCreatedV1.WireName);
        services.AddSingleton(new AuditFailures(auditFailures));
    }
}

internal sealed record AuditFailures(int Count);

/// <summary>An internal Integration Event Handler with no Wolverine type: its effect joins the delivery's transaction.</summary>
internal sealed class ReserveStockOnProductCreated(ModuleTransaction transaction) : IIntegrationEventHandler<ProductCreatedV1>
{
    public async Task<Result> Handle(ProductCreatedV1 message, CancellationToken cancellationToken)
    {
        await transaction.ExecuteAsync(
            "insert into inventory.effects (subscription, product_id) values ($1, $2)",
            InventoryModule.ReserveStock,
            message.ProductId
        );
        return Result.Success();
    }
}

/// <summary>An internal handler that applies its effect and then returns a failed Result for its first attempts.</summary>
internal sealed class AuditProductOnProductCreated(ModuleTransaction transaction, DeliveryAttempt attempt, AuditFailures failures)
    : IIntegrationEventHandler<ProductCreatedV1>
{
    public async Task<Result> Handle(ProductCreatedV1 message, CancellationToken cancellationToken)
    {
        // Written before the failure, so a failed attempt that left it behind would show in the effects table.
        await transaction.ExecuteAsync(
            "insert into inventory.effects (subscription, product_id) values ($1, $2)",
            InventoryModule.AuditProduct,
            message.ProductId
        );
        return attempt.PriorFailures < failures.Count
            ? Result.Failure($"audit refused attempt {attempt.PriorFailures + 1}")
            : Result.Success();
    }
}
