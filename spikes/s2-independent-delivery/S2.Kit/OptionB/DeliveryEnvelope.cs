using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace S2.Kit.OptionB;

/// <summary>
/// Option B: a Kit-owned delivery envelope per handler. At publish time the Kit fans one message out into one
/// <see cref="HandlerDelivery{TMessage}"/> per subscription. Each is its own durable envelope; one Kit-owned Wolverine
/// handler per message type resolves the subscribed handler by its subscription name.
/// </summary>
/// <param name="queuePerSubscription">
/// False (the option as chosen): every delivery of one message type is published to one durable local queue named
/// <c>kit.delivery.{wire name}</c>. True (observed variant, rejected): each delivery is sent to its subscription's own
/// queue with <c>EndpointFor(name).SendAsync</c>, which stores the row as <c>Outgoing</c> with owner 0.
/// </param>
public sealed class DeliveryEnvelope(bool queuePerSubscription = false) : IDeliveryOption
{
    private KitMessaging _messaging = null!;

    public void Configure(KitMessaging messaging, IServiceCollection services, WolverineOptions opts)
    {
        _messaging = messaging;
        foreach (var s in messaging.Subscriptions)
        {
            services.AddKeyedScoped(typeof(IIntegrationEventHandler<>).MakeGenericType(s.MessageType), s.Name, s.HandlerType);
            if (queuePerSubscription) opts.LocalQueue(s.Name);
        }

        foreach (var messageType in messaging.Subscriptions.Select(s => s.MessageType).Distinct())
        {
            var deliveryType = typeof(HandlerDelivery<>).MakeGenericType(messageType);
            var wireName = "kit.delivery." + WireNames.Of(messageType);
            WireNames.Register(deliveryType, wireName);
            opts.Discovery.IncludeType(typeof(HandlerDeliveryHandler<>).MakeGenericType(messageType));
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

/// <summary>The Kit's delivery envelope: one message for one subscription.</summary>
public sealed record HandlerDelivery<TMessage>(string Subscription, TMessage Message);

/// <summary>The one Kit-owned Wolverine handler per message type.</summary>
public sealed class HandlerDeliveryHandler<TMessage>(IServiceScopeFactory scopes)
{
    public async Task Handle(HandlerDelivery<TMessage> delivery, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventHandler<TMessage>>(delivery.Subscription);
        var result = await handler.Handle(delivery.Message, ct);
        if (result.IsFailure) throw new HandlerFailedException(delivery.Subscription, result.Error!);
    }
}
