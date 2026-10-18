using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;
using SagaSpike.Contracts;
using SagaSpike.Kit;

namespace SagaSpike.Shipping;

/// <summary>The Shipping Module's Composition Root: the fulfilment saga and the Integration Events it emits.</summary>
public sealed class ShippingModule : IModuleDefinition
{
    /// <inheritdoc />
    public void Configure(ModuleBuilder module)
    {
        module.Publish<OrderFulfilledV1>("shipping.order-fulfilled.v1");
        module.Publish<OrderCancelledV1>("shipping.order-cancelled.v1");
        module.AddSpikeSaga<FulfilmentSaga>(
            "shipping.fulfilment",
            systemActor: "shipping.fulfilment",
            saga => saga
                .Input<OrderPlacedV1>(placed => placed.OrderId)
                .Input<PaymentReceivedV1>(paid => paid.OrderId)
                .Input<PaymentReceivedV2>(paid => paid.OrderId)
                .Timeout<FulfilmentTimeout>()
        );
    }
}
