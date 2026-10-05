using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace S2.Kit;

/// <summary>
/// One handler subscribed to one message type. <see cref="Name"/> is the transport-neutral, durable subscription name
/// declared by the Module; options that give each subscription its own queue use it as the queue name.
/// </summary>
public sealed record Subscription(
    Type MessageType,
    Type HandlerType,
    string Name,
    Func<IServiceProvider, object, CancellationToken, Task<Result>> Invoke)
{
    /// <summary>
    /// One delivery attempt, the same for every option: a new scope (the stand-in for the Module's scope, Unit of Work
    /// and Receipt), the handler, and a failed Result turned into an exception so that the transport retries.
    /// </summary>
    public async Task DeliverAsync(IServiceScopeFactory scopes, object message, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var result = await Invoke(scope.ServiceProvider, message, ct);
        if (result.IsFailure) throw new HandlerFailedException(Name, result.Error!);
    }
}

/// <summary>
/// Spike stand-in for the Kit's messaging registration, as a Module's Composition Root calls it. Modules declare
/// wire names and subscriptions with no transport type; an <see cref="IDeliveryOption"/> turns them into Wolverine
/// configuration. Subscription names are compared ignoring case, because Wolverine lower-cases local queue names.
/// </summary>
public sealed class KitMessaging
{
    private readonly Dictionary<string, Subscription> _byName = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<Subscription> Subscriptions => _byName.Values;

    public void Publish<TMessage>(string wireName) => WireNames.Register(typeof(TMessage), wireName);

    public void Subscribe<TMessage, THandler>(string subscription)
        where THandler : class, IIntegrationEventHandler<TMessage>
        => Add(new(typeof(TMessage), typeof(THandler), subscription,
            (services, message, ct) => services.GetRequiredService<THandler>().Handle((TMessage)message, ct)));

    public void SubscribeNotification<TDomainEvent, THandler>(string subscription)
        where THandler : class, INotificationHandler<TDomainEvent>
        => Add(new(typeof(TDomainEvent), typeof(THandler), subscription,
            (services, domainEvent, ct) => services.GetRequiredService<THandler>().Handle((TDomainEvent)domainEvent, ct)));

    public Subscription Named(string subscription) => _byName[subscription];

    public IEnumerable<Subscription> For(Type messageType) => _byName.Values.Where(s => s.MessageType == messageType);

    public void AddHandlers(IServiceCollection services)
    {
        services.AddSingleton(this);
        foreach (var s in Subscriptions) services.AddScoped(s.HandlerType);
    }

    private void Add(Subscription subscription)
    {
        if (!_byName.TryAdd(subscription.Name, subscription))
            throw new InvalidOperationException($"Subscription '{subscription.Name}' is declared twice (names ignore case).");
    }
}

/// <summary>How the Kit turns subscriptions into independent delivery. One implementation per Spike option.</summary>
public interface IDeliveryOption
{
    void Configure(KitMessaging messaging, IServiceCollection services, WolverineOptions opts);

    /// <summary>Called by the Unit of Work, inside its transaction, once per message the Operation published.</summary>
    ValueTask PublishAsync(IMessageBus outbox, object message);
}
