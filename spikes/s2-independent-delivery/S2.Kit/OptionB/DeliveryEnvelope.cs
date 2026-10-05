using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace S2.Kit.OptionB;

/// <summary>
/// Option B: a Kit-owned delivery envelope per handler. At publish time the Kit fans one message out into one
/// <see cref="HandlerDelivery{TMessage}"/> per subscription. Each is its own durable envelope; one Kit-owned Wolverine
/// handler per message type finds the subscription by the name the envelope carries.
/// </summary>
/// <param name="queuePerSubscription">
/// False (the option as recorded): every delivery of one message type is published to one durable local queue named
/// <c>kit.delivery.{wire name}</c>. True (observed variant, rejected): each delivery is sent to its subscription's own
/// queue with <c>EndpointFor(name).SendAsync</c>, which stores the row as <c>Outgoing</c> with owner 0.
/// </param>
/// <param name="prebuilt">
/// False (the option as recorded): <see cref="HandlerDeliveryHandler{TMessage}"/>, for which Wolverine generates code
/// that names the message type. True (repair variant): one pre-built <see cref="PrebuiltDeliveryHandler"/> per
/// message type, registered with <c>AddMessageHandler</c>, so that no code is generated.
/// </param>
public sealed class DeliveryEnvelope(bool queuePerSubscription = false, bool prebuilt = false) : IDeliveryOption
{
    private KitMessaging _messaging = null!;

    public void Configure(KitMessaging messaging, IServiceCollection services, WolverineOptions opts)
    {
        _messaging = messaging;
        if (queuePerSubscription)
            foreach (var s in messaging.Subscriptions) opts.LocalQueue(s.Name);

        foreach (var messageType in messaging.Subscriptions.Select(s => s.MessageType).Distinct())
        {
            var deliveryType = typeof(HandlerDelivery<>).MakeGenericType(messageType);
            var wireName = "kit.delivery." + WireNames.Of(messageType);
            WireNames.Register(deliveryType, wireName);
            if (prebuilt) opts.AddMessageHandler(deliveryType, new PrebuiltDeliveryHandler(messaging));
            else opts.Discovery.IncludeType(typeof(HandlerDeliveryHandler<>).MakeGenericType(messageType));
            if (!queuePerSubscription) opts.PublishMessage(deliveryType).ToLocalQueue(wireName);
        }
    }

    public async ValueTask PublishAsync(IMessageBus outbox, object message)
    {
        var deliveryType = typeof(HandlerDelivery<>).MakeGenericType(message.GetType());
        foreach (var s in _messaging.For(message.GetType()))
        {
            var delivery = Activator.CreateInstance(deliveryType, s.Name, message)!;
            if (queuePerSubscription) await outbox.EndpointFor(s.Name).SendAsync(delivery);
            else await outbox.PublishAsync(delivery);
        }
    }
}

public interface IHandlerDelivery
{
    string Subscription { get; }
    object Payload { get; }
}

/// <summary>The Kit's delivery envelope: one message for one subscription.</summary>
public sealed record HandlerDelivery<TMessage>(string Subscription, TMessage Message) : IHandlerDelivery
{
    object IHandlerDelivery.Payload => Message!;
}

/// <summary>The one Kit-owned, generated Wolverine handler per message type.</summary>
public sealed class HandlerDeliveryHandler<TMessage>(KitMessaging messaging, IServiceScopeFactory scopes)
{
    public Task Handle(HandlerDelivery<TMessage> delivery, CancellationToken ct) =>
        messaging.Named(delivery.Subscription).DeliverAsync(scopes, delivery.Message!, ct);
}

/// <summary>The pre-built alternative: no generated code.</summary>
public sealed class PrebuiltDeliveryHandler(KitMessaging messaging) : MessageHandler
{
    public override Task HandleAsync(MessageContext context, CancellationToken ct)
    {
        var delivery = (IHandlerDelivery)context.Envelope!.Message!;
        return messaging.Named(delivery.Subscription).DeliverAsync(
            context.Runtime.Services.GetRequiredService<IServiceScopeFactory>(), delivery.Payload, ct);
    }
}
