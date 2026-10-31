using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;
using PouryaKit.Persistence.EFCore;
using PouryaKit.Persistence.Postgres;

namespace ModularMonolith.Tests.Sagas;

/// <summary>The Billing Module's Composition Root: tenant-scoped, with an ordinary subscription to each event it bills.</summary>
public sealed class BillingModule : IModuleDefinition
{
    /// <summary>The Module's schema.</summary>
    public const string Schema = "billing";

    /// <inheritdoc />
    public void Configure(ModuleBuilder module)
    {
        module.AddPouryaKitTenancy();
        module.UsePouryaKitPostgres<BillingDbContext>(connectionName: "app", schema: Schema);
        module.AddPouryaKitCqrs();
        module.Subscribe<OrderPlacedV1, OpenInvoice>("billing.order-placed");
        module.Subscribe<OrderFulfilledV1, CloseInvoice>("billing.order-fulfilled");
        module.Subscribe<OrderCancelledV1, VoidInvoice>("billing.order-cancelled");
    }
}

/// <summary>What one of Billing's deliveries received and the Operation Context it ran under.</summary>
internal sealed record BillingDelivery(
    string Subscription,
    Guid OrderId,
    OperationContext Context,
    string? TraceId,
    string? TraceState
);

/// <summary>Every delivery Billing applied in this process, and the deliveries a test holds.</summary>
internal static class BillingLog
{
    private static readonly ConcurrentQueue<BillingDelivery> Deliveries = [];
    private static readonly ConcurrentDictionary<Guid, TaskCompletionSource> Held = [];

    /// <summary>Billing's deliveries of the order <paramref name="orderId"/>.</summary>
    public static List<BillingDelivery> Of(Guid orderId) =>
        [.. Deliveries.Where(delivery => delivery.OrderId == orderId)];

    /// <summary>Holds each delivery of <paramref name="orderId"/>'s fulfilment before it applies, until the returned release.</summary>
    public static Action Hold(Guid orderId)
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Held[orderId] = held;
        return () =>
        {
            Held.TryRemove(orderId, out _);
            held.TrySetResult();
        };
    }

    internal static async Task AddAsync(string subscription, Guid orderId, OperationContext context)
    {
        if (Held.TryGetValue(orderId, out var held))
        {
            await held.Task;
        }

        var activity = System.Diagnostics.Activity.Current;
        Deliveries.Enqueue(
            new BillingDelivery(
                subscription,
                orderId,
                context,
                activity?.TraceId.ToString(),
                activity?.TraceStateString
            )
        );
    }
}

internal sealed class BillingDbContext(DbContextOptions<BillingDbContext> options)
    : PouryaKitDbContext(options);

internal sealed class OpenInvoice(OperationContext context)
    : IIntegrationEventHandler<OrderPlacedV1>
{
    public async Task<Result> Handle(OrderPlacedV1 placed, CancellationToken cancellationToken)
    {
        await BillingLog.AddAsync("billing.order-placed", placed.OrderId, context);
        return Result.Success();
    }
}

internal sealed class CloseInvoice(OperationContext context)
    : IIntegrationEventHandler<OrderFulfilledV1>
{
    public async Task<Result> Handle(
        OrderFulfilledV1 fulfilled,
        CancellationToken cancellationToken
    )
    {
        await BillingLog.AddAsync("billing.order-fulfilled", fulfilled.OrderId, context);
        return Result.Success();
    }
}

internal sealed class VoidInvoice(OperationContext context)
    : IIntegrationEventHandler<OrderCancelledV1>
{
    public async Task<Result> Handle(
        OrderCancelledV1 cancelled,
        CancellationToken cancellationToken
    )
    {
        await BillingLog.AddAsync("billing.order-cancelled", cancelled.OrderId, context);
        return Result.Success();
    }
}
