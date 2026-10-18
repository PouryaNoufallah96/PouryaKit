using SagaSpike.Contracts;
using SagaSpike.Kit;
using Wolverine;

namespace SagaSpike.Shipping;

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

    /// <summary>Starts the saga, and schedules its timeout.</summary>
    public static (FulfilmentSaga, FulfilmentTimeout) Start(SagaInput<FulfilmentSaga, OrderPlacedV1> placed) =>
        (
            new FulfilmentSaga { Order = placed.Event.OrderId, Installments = placed.Event.Installments },
            new FulfilmentTimeout(TimeSpan.FromSeconds(placed.Event.PayWithinSeconds))
        );

    /// <summary>One payment arrived; the last one fulfils the order.</summary>
    public async Task Handle(SagaInput<FulfilmentSaga, PaymentReceivedV1> paid, IMessageContext context)
    {
        if (SagaProbes.InPaymentStep is { } inStep)
        {
            await inStep();
        }

        Paid++;
        if (Paid >= Installments)
        {
            await PublishAsync(context, new OrderFulfilledV1(Order, Paid));
            MarkCompleted();
        }
    }

    /// <summary>The same payment, under its second contract.</summary>
    public void Handle(SagaInput<FulfilmentSaga, PaymentReceivedV2> paid) => Methods.Add(paid.Event.Method);

    /// <summary>The payments did not arrive in time.</summary>
    public async Task Handle(FulfilmentTimeout timeout, IMessageContext context)
    {
        await PublishAsync(context, new OrderCancelledV1(Order, Paid));
        MarkCompleted();
    }
}

/// <summary>The fulfilment saga's timeout. It has no identity member, so it keeps the identity it was scheduled under.</summary>
public sealed record FulfilmentTimeout(TimeSpan Within) : TimeoutMessage(Within);

/// <summary>A probe a test's child process sets, run inside a payment step before it changes anything.</summary>
public static class SagaProbes
{
    /// <summary>Run at the start of each payment step, inside its transaction.</summary>
    public static Func<Task>? InPaymentStep { get; set; }
}
