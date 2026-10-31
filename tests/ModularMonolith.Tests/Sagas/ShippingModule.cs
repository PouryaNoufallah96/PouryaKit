using PouryaKit.Messaging.Wolverine;
using PouryaKit.Messaging.Wolverine.Sagas;
using PouryaKit.Modularity;

namespace ModularMonolith.Tests.Sagas;

/// <summary>The Shipping Module's Composition Root: its Sagas and the Integration Events they emit.</summary>
public sealed class ShippingModule : IModuleDefinition
{
    /// <summary>The fulfilment saga's name and queue, and its system Actor.</summary>
    public const string Fulfilment = "shipping.fulfilment";

    /// <inheritdoc />
    public void Configure(ModuleBuilder module)
    {
        module.Publish<OrderFulfilledV1>("shipping.order-fulfilled.v1");
        module.Publish<OrderCancelledV1>("shipping.order-cancelled.v1");
        #region fulfilment-saga-declaration
        module.AddPouryaKitSaga<FulfilmentSaga>(
            Fulfilment,
            systemActor: Fulfilment,
            saga =>
                saga.Input<OrderPlacedV1>(placed => placed.OrderId)
                    .Input<PaymentReceivedV1>(paid => paid.OrderId)
                    .Input<PaymentReceivedV2>(paid => paid.OrderId)
                    .Timeout<FulfilmentTimeout>()
        );
        #endregion
        // Two sagas of one event: each its own chain, on its own queue.
        module.AddPouryaKitSaga<ReferenceSaga>(
            "shipping.reference",
            "shipping.reference",
            saga => saga.Input<ReferenceOpenedV1>(opened => opened.Reference)
        );
        module.AddPouryaKitSaga<ReminderSaga>(
            "shipping.reminder",
            "shipping.reminder",
            saga =>
                saga.Input<ReferenceOpenedV1>(opened => opened.Reference).Timeout<ReminderTimeout>()
        );
        module.AddPouryaKitSaga<TallySaga>(
            "shipping.tally",
            "shipping.tally",
            saga => saga.Input<ReferenceOpenedV1>(opened => opened.Reference)
        );
    }
}

/// <summary>Fulfils an order once all its payments arrive, or cancels it when they do not arrive in time.</summary>
public sealed class FulfilmentSaga : PouryaKitSaga
{
    /// <summary>The order.</summary>
    public Guid Order { get; set; }

    /// <summary>How many payments the order needs.</summary>
    public int Installments { get; set; }

    /// <summary>How many payments arrived, under the first payment contract.</summary>
    public int Paid { get; set; }

    /// <summary>The payment methods that arrived, under the second payment contract.</summary>
    public List<string> Methods { get; set; } = [];

    #region fulfilment-saga-start
    /// <summary>Starts the saga, and schedules its timeout.</summary>
    public static (FulfilmentSaga, FulfilmentTimeout) Start(
        SagaInput<FulfilmentSaga, OrderPlacedV1> placed
    ) =>
        (
            new FulfilmentSaga
            {
                Order = placed.Event.OrderId,
                Installments = placed.Event.Installments,
            },
            new FulfilmentTimeout(TimeSpan.FromSeconds(placed.Event.PayWithinSeconds))
        );
    #endregion

    /// <summary>One payment arrived; the last one fulfils the order.</summary>
    public async Task Handle(SagaInput<FulfilmentSaga, PaymentReceivedV1> paid)
    {
        if (SagaProbes.InPaymentStep is { } inStep)
        {
            await inStep();
        }

        Paid++;
        if (Paid >= Installments)
        {
            await PublishAsync(new OrderFulfilledV1(Order, Paid));
            MarkCompleted();
        }
    }

    /// <summary>The same payment, under its second contract; the method "twice" emits one event twice from one step.</summary>
    public async Task Handle(SagaInput<FulfilmentSaga, PaymentReceivedV2> paid)
    {
        Methods.Add(paid.Event.Method);
        if (paid.Event.Method == "twice")
        {
            await PublishAsync(new OrderFulfilledV1(Order, Paid));
            await PublishAsync(new OrderFulfilledV1(Order, Paid));
        }
    }

    #region fulfilment-saga-timeout
    /// <summary>The payments did not arrive in time.</summary>
    public async Task Handle(FulfilmentTimeout timeout)
    {
        await PublishAsync(new OrderCancelledV1(Order, Paid));
        MarkCompleted();
    }
    #endregion
}

/// <summary>The fulfilment saga's timeout.</summary>
public sealed record FulfilmentTimeout(TimeSpan Within) : SagaTimeout(Within);

/// <summary>A saga keyed by a text business id, so two tenants' identities that join alike can be tried.</summary>
public sealed class ReferenceSaga : PouryaKitSaga
{
    /// <summary>The reference, as the event named it.</summary>
    public string Reference { get; set; } = "";

    /// <summary>Starts the saga.</summary>
    public static ReferenceSaga Start(SagaInput<ReferenceSaga, ReferenceOpenedV1> opened) =>
        new() { Reference = opened.Event.Reference };
}

/// <summary>A saga whose starting step schedules two timeouts of one type.</summary>
public sealed class ReminderSaga : PouryaKitSaga
{
    /// <summary>The delays of the reminders that applied, in seconds.</summary>
    public List<int> Reminded { get; set; } = [];

    /// <summary>Starts the saga, and schedules two reminders.</summary>
    public static (ReminderSaga, ReminderTimeout, ReminderTimeout) Start(
        SagaInput<ReminderSaga, ReferenceOpenedV1> opened
    ) =>
        (
            new ReminderSaga(),
            new ReminderTimeout(TimeSpan.FromSeconds(1)),
            new ReminderTimeout(TimeSpan.FromSeconds(2))
        );

    /// <summary>A reminder is due.</summary>
    public void Handle(ReminderTimeout reminder) => Reminded.Add((int)reminder.Within.TotalSeconds);
}

/// <summary>A saga whose one step, under Wolverine's Async name, both starts it and advances it.</summary>
public sealed class TallySaga : PouryaKitSaga
{
    /// <summary>How many references were opened.</summary>
    public int Count { get; set; }

    /// <summary>Starts the saga, or advances it.</summary>
    public Task StartOrHandleAsync(SagaInput<TallySaga, ReferenceOpenedV1> opened)
    {
        ArgumentNullException.ThrowIfNull(opened);
        Count++;
        return Task.CompletedTask;
    }
}

/// <summary>The reminder saga's timeout, of which one step schedules two.</summary>
public sealed record ReminderTimeout(TimeSpan Within) : SagaTimeout(Within);

/// <summary>A probe the crash test's child process sets, run inside a payment step before it changes anything.</summary>
internal static class SagaProbes
{
    /// <summary>Run at the start of each payment step, inside its transaction.</summary>
    public static Func<Task>? InPaymentStep { get; set; }
}
