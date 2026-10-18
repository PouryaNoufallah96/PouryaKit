using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;
using PouryaKit.Persistence.EFCore;
using PouryaKit.Persistence.Postgres;
using SagaSpike.Contracts;

namespace SagaSpike.Billing;

/// <summary>The Billing Module's Composition Root: tenant-scoped, with three ordinary subscriptions.</summary>
public sealed class BillingModule : IModuleDefinition
{
    /// <inheritdoc />
    public void Configure(ModuleBuilder module)
    {
        module.AddPouryaKitTenancy();
        module.UsePouryaKitPostgres<BillingDbContext>(connectionName: "app", schema: "billing");
        module.AddPouryaKitCqrs();
        module.Subscribe<OrderPlacedV1, OpenInvoice>("billing.order-placed");
        module.Subscribe<OrderFulfilledV1, CloseInvoice>("billing.order-fulfilled");
        module.Subscribe<OrderCancelledV1, VoidInvoice>("billing.order-cancelled");
    }
}

/// <summary>What one of Billing's deliveries received and the Operation Context it ran under.</summary>
public sealed record BillingDelivery(string Subscription, Guid OrderId, OperationContext Context);

/// <summary>Every delivery Billing applied in this process, in order.</summary>
public static class BillingLog
{
    private static readonly ConcurrentQueue<BillingDelivery> Deliveries = [];

    /// <summary>Billing's deliveries of the order <paramref name="orderId"/>.</summary>
    public static List<BillingDelivery> Of(Guid orderId) => [.. Deliveries.Where(delivery => delivery.OrderId == orderId)];

    internal static void Add(string subscription, Guid orderId, OperationContext context) =>
        Deliveries.Enqueue(new BillingDelivery(subscription, orderId, context));
}

internal sealed class BillingDbContext(DbContextOptions<BillingDbContext> options) : PouryaKitDbContext(options);

internal sealed class OpenInvoice(OperationContext context) : IIntegrationEventHandler<OrderPlacedV1>
{
    public Task<Result> Handle(OrderPlacedV1 placed, CancellationToken cancellationToken)
    {
        BillingLog.Add("billing.order-placed", placed.OrderId, context);
        return Task.FromResult(Result.Success());
    }
}

internal sealed class CloseInvoice(OperationContext context) : IIntegrationEventHandler<OrderFulfilledV1>
{
    public Task<Result> Handle(OrderFulfilledV1 fulfilled, CancellationToken cancellationToken)
    {
        BillingLog.Add("billing.order-fulfilled", fulfilled.OrderId, context);
        return Task.FromResult(Result.Success());
    }
}

internal sealed class VoidInvoice(OperationContext context) : IIntegrationEventHandler<OrderCancelledV1>
{
    public Task<Result> Handle(OrderCancelledV1 cancelled, CancellationToken cancellationToken)
    {
        BillingLog.Add("billing.order-cancelled", cancelled.OrderId, context);
        return Task.FromResult(Result.Success());
    }
}
