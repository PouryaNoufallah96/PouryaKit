using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;
using PouryaKit.Persistence;
using PouryaKit.Persistence.EFCore;
using PouryaKit.Persistence.Postgres;

namespace ModularMonolith.Tests.Sagas;

/// <summary>The Orders Module's Composition Root: it places orders and records payments, and publishes both.</summary>
public sealed class OrdersModule : IModuleDefinition
{
    /// <summary>The Module's schema.</summary>
    public const string Schema = "orders";

    /// <inheritdoc />
    public void Configure(ModuleBuilder module)
    {
        module.UsePouryaKitPostgres<OrdersDbContext>(connectionName: "app", schema: Schema);
        module.AddPouryaKitCqrs();
        module.AddPouryaKitNotificationHandlers();
        module.Publish<OrderPlacedV1>("orders.order-placed.v1");
        module.Publish<PaymentReceivedV1>("orders.payment-received.v1");
        module.Publish<PaymentReceivedV2>("orders.payment-received.v2");
        module.Publish<ReferenceOpenedV1>("orders.reference-opened.v1");
    }
}

internal sealed record PlaceOrderCommand(Guid OrderId, int Installments, int PayWithinSeconds)
    : Command;

internal sealed record RecordPaymentCommand(Guid OrderId) : Command;

internal sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options)
    : PouryaKitDbContext(options);

internal sealed record OrderId(Guid Value) : TypedId<Guid>(Value);

internal sealed class Order : AggregateRoot<OrderId>
{
    private Order() { }

    public int Installments { get; private set; }

    public int PayWithinSeconds { get; private set; }

    public static Order Place(OrderId id, int installments, int payWithinSeconds)
    {
        var order = new Order
        {
            Id = id,
            Installments = installments,
            PayWithinSeconds = payWithinSeconds,
        };
        order.Raise(new OrderPlaced(id.Value, installments, payWithinSeconds));
        return order;
    }

    public void RecordPayment() => Raise(new PaymentRecorded(Id.Value));
}

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder) => builder.ToTable("orders");
}

internal sealed record OrderPlaced(Guid OrderId, int Installments, int PayWithinSeconds)
    : DomainEvent;

internal sealed record PaymentRecorded(Guid OrderId) : DomainEvent;

internal sealed class PlaceOrderHandler(IRepository<Order, OrderId> orders)
    : CommandHandler<PlaceOrderCommand>
{
    public override async Task<Result> Handle(
        PlaceOrderCommand command,
        CancellationToken cancellationToken
    )
    {
        await orders.AddAsync(
            Order.Place(
                new OrderId(command.OrderId),
                command.Installments,
                command.PayWithinSeconds
            ),
            cancellationToken
        );
        return Result.Success();
    }
}

internal sealed class RecordPaymentHandler(IRepository<Order, OrderId> orders)
    : CommandHandler<RecordPaymentCommand>
{
    public override async Task<Result> Handle(
        RecordPaymentCommand command,
        CancellationToken cancellationToken
    )
    {
        var order = await orders.FindAsync(new OrderId(command.OrderId), cancellationToken);
        order!.RecordPayment();
        return Result.Success();
    }
}

internal sealed class PublishOrderPlaced(IIntegrationEventPublisher events)
    : INotificationHandler<OrderPlaced>
{
    public Task<Result> Handle(OrderPlaced placed, CancellationToken cancellationToken)
    {
        events.Publish(
            placed.Id,
            new OrderPlacedV1(placed.OrderId, placed.Installments, placed.PayWithinSeconds)
        );
        return Task.FromResult(Result.Success());
    }
}

// One Domain Event, two contracts: both carry its identity, as Catalog's V1 and V2 of one product do.
internal sealed class PublishPaymentReceived(IIntegrationEventPublisher events)
    : INotificationHandler<PaymentRecorded>
{
    public Task<Result> Handle(PaymentRecorded recorded, CancellationToken cancellationToken)
    {
        events.Publish(recorded.Id, new PaymentReceivedV1(recorded.OrderId));
        events.Publish(recorded.Id, new PaymentReceivedV2(recorded.OrderId, "card"));
        return Task.FromResult(Result.Success());
    }
}
