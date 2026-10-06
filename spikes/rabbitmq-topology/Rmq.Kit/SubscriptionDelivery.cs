using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Rmq.Kit;

/// <summary>
/// Which listening endpoint serves which subscription. Under topology A every subscription's broker queue maps to its
/// subscription. Under topology B the Host's one broker queue maps to "fan out", and each subscription's local queue to
/// its subscription, as ADR 0006 does today.
/// </summary>
public sealed class EndpointMap
{
    private readonly Dictionary<string, Subscription?> _targets = new(StringComparer.OrdinalIgnoreCase);

    public void Deliver(Uri endpoint, Subscription subscription) => _targets.Add(Key(endpoint), subscription);

    public void FanOut(Uri endpoint) => _targets.Add(Key(endpoint), null);

    /// <summary>The subscription an envelope received at <paramref name="endpoint"/> is for, or null to fan it out.</summary>
    public bool TryResolve(Uri endpoint, out Subscription? subscription) =>
        _targets.TryGetValue(Key(endpoint), out subscription);

    private static string Key(Uri endpoint) => endpoint.ToString().TrimEnd('/');
}

/// <summary>Test-only hooks into the delivery step.</summary>
public sealed class SpikeHooks
{
    /// <summary>Runs after topology B stored the local copies of a broker envelope, before that envelope is marked handled.</summary>
    public Func<Envelope, Task>? AfterFanOut { get; set; }
}

/// <summary>
/// The one pre-built handler of a message type (ADR 0006: <c>AddMessageHandler</c>, no generated code, so the message
/// and its handlers may be internal). The endpoint the envelope arrived at decides what happens: fan out to the local
/// queues (topology B's broker queue), or deliver to the subscription the endpoint serves.
/// </summary>
public sealed class SubscriptionDelivery(EndpointMap endpoints) : MessageHandler
{
    public override async Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        var envelope = context.Envelope!;
        if (!endpoints.TryResolve(envelope.Destination!, out var subscription))
        {
            throw new InvalidOperationException($"No subscription is served at {envelope.Destination}.");
        }

        if (subscription is null)
        {
            await FanOutAsync(context, envelope);
            return;
        }

        await DeliverAsync(context.Runtime.Services, subscription, envelope, cancellation);
    }

    // Topology B: publish the message again inside this Host, where it has one route per subscription to a durable
    // local queue, so each copy is a new envelope with an id of its own. The Kit's headers travel with it.
    private static async Task FanOutAsync(MessageContext context, Envelope envelope)
    {
        var options = new DeliveryOptions();
        foreach (var (name, value) in envelope.Headers)
        {
            if (name.StartsWith("pouryakit-", StringComparison.Ordinal))
            {
                options.Headers[name] = value;
            }
        }

        await context.PublishAsync(envelope.Message!, options);
        // Wolverine flushes after the handler returns anyway; flushing here lets a test hold in the window between the
        // local copies being stored and the broker envelope being marked handled.
        await context.FlushOutgoingMessagesAsync();
        if (context.Runtime.Services.GetRequiredService<SpikeHooks>().AfterFanOut is { } hook)
        {
            await hook(envelope);
        }
    }

    // The shared delivery step: a scope, the Module's transaction, the Receipt, the handler, and a failed Result turned
    // into an exception so the delivery is retried.
    private static async Task DeliverAsync(
        IServiceProvider services,
        Subscription subscription,
        Envelope envelope,
        CancellationToken cancellation
    )
    {
        var messageId = envelope.Headers.TryGetValue(KitHeaders.DomainEventId, out var header) && header is not null
            ? Guid.Parse(header)
            : throw new InvalidOperationException($"The envelope {envelope.Id} has no {KitHeaders.DomainEventId} header.");
        var log = services.GetRequiredService<AttemptLog>();
        var receivedAt = envelope.Destination!.ToString();

        await using var scope = services.CreateAsyncScope();
        var attempt = scope.ServiceProvider.GetRequiredService<DeliveryAttempt>();
        attempt.Subscription = subscription.Name;
        attempt.MessageId = messageId;
        attempt.PriorFailures = await log.FailuresAsync(subscription.Name, messageId);

        var transaction = scope.ServiceProvider.GetRequiredService<ModuleTransaction>();
        await transaction.BeginAsync(cancellation);
        if (!await transaction.RecordReceiptAsync(subscription.Name, messageId))
        {
            await transaction.CommitAsync();
            await log.AddAsync(subscription.Name, messageId, envelope.Id, receivedAt, "duplicate");
            return;
        }

        var result = await subscription.Invoke(scope.ServiceProvider, envelope.Message!, cancellation);
        if (result.IsFailure)
        {
            await transaction.RollbackAsync();
            await log.AddAsync(subscription.Name, messageId, envelope.Id, receivedAt, "failed");
            throw new HandlerFailedException(subscription.Name, result.Error!);
        }

        await transaction.CommitAsync();
        await log.AddAsync(subscription.Name, messageId, envelope.Id, receivedAt, "succeeded");
    }
}
