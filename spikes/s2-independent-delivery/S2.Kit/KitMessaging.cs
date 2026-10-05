using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace S2.Kit;

/// <summary>
/// One handler subscribed to one message type. <see cref="Name"/> is the transport-neutral subscription name,
/// declared by the receiving Module; both options use it as the durable local queue name.
/// </summary>
public sealed record Subscription(Type MessageType, Type HandlerType, string Name);

/// <summary>
/// Spike stand-in for the Kit's messaging registration, as a Module's Composition Root calls it. Modules declare
/// wire names and subscriptions with no transport type; a <see cref="IDeliveryOption"/> turns them into Wolverine
/// configuration.
/// </summary>
public sealed class KitMessaging
{
    private readonly List<Subscription> _subscriptions = [];

    public IReadOnlyList<Subscription> Subscriptions => _subscriptions;

    public void Publish<TMessage>(string wireName) => WireNames.Register(typeof(TMessage), wireName);

    public void Subscribe<TMessage, THandler>(string subscription)
        where THandler : class, IIntegrationEventHandler<TMessage>
        => _subscriptions.Add(new Subscription(typeof(TMessage), typeof(THandler), subscription));

    public IEnumerable<Subscription> For(Type messageType) => _subscriptions.Where(s => s.MessageType == messageType);
}

/// <summary>How the Kit turns subscriptions into independent delivery. One implementation per Spike option.</summary>
public interface IDeliveryOption
{
    void Configure(KitMessaging messaging, IServiceCollection services, WolverineOptions opts);

    /// <summary>Called by the Unit of Work, inside its transaction, once per message the Operation published.</summary>
    ValueTask PublishAsync(IMessageBus outbox, object message);
}
