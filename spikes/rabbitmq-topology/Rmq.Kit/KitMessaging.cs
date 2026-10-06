using Microsoft.Extensions.DependencyInjection;

namespace Rmq.Kit;

/// <summary>One handler subscribed to one message type under a durable, lower-cased subscription name.</summary>
public sealed record Subscription(
    Type MessageType,
    Type HandlerType,
    string Name,
    Func<IServiceProvider, object, CancellationToken, Task<Result>> Invoke
);

/// <summary>
/// Spike stand-in for the Kit's declarations, as a Module's Composition Root calls them. They name no transport: the
/// topology turns them into Wolverine and RabbitMQ configuration.
/// </summary>
public sealed class KitMessaging
{
    private readonly Dictionary<string, Subscription> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Type, string> _publications = [];

    public IEnumerable<Subscription> Subscriptions => _byName.Values;

    /// <summary>What this Host's Modules publish, by type, with each type's wire name.</summary>
    public IReadOnlyDictionary<Type, string> Publications => _publications;

    /// <summary>Declares that a Module of this Host publishes <typeparamref name="TMessage"/> under <paramref name="wireName"/>.</summary>
    public void Publish<TMessage>(string wireName)
    {
        WireNames.Register(typeof(TMessage), wireName);
        _publications[typeof(TMessage)] = wireName;
    }

    /// <summary>
    /// Subscribes a handler to an Integration Event published by another Host. The wire name comes with the Contracts,
    /// because no Module of this Host publishes the type.
    /// </summary>
    public void Subscribe<TMessage, THandler>(string subscription, string wireName)
        where THandler : class, IIntegrationEventHandler<TMessage>
    {
        WireNames.Register(typeof(TMessage), wireName);
        var name = subscription.ToLowerInvariant();
        if (
            !_byName.TryAdd(
                name,
                new Subscription(
                    typeof(TMessage),
                    typeof(THandler),
                    name,
                    (scope, message, cancellationToken) =>
                        scope.GetRequiredService<THandler>().Handle((TMessage)message, cancellationToken)
                )
            )
        )
        {
            throw new InvalidOperationException($"The subscription '{name}' is declared twice.");
        }
    }

    public void AddHandlers(IServiceCollection services)
    {
        services.AddSingleton(this);
        foreach (var subscription in Subscriptions)
        {
            services.AddScoped(subscription.HandlerType);
        }
    }
}
