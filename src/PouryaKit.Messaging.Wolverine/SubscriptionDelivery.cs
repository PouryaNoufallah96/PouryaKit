using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace PouryaKit.Messaging.Wolverine;

// The one pre-built handler of a message type, for every subscription to it: Wolverine routes one envelope to each
// subscription's own queue, so the queue an envelope arrived on names the subscription, and each is retried and
// acknowledged on its own. It generates no code, so the message and its handlers may be internal.
internal sealed class SubscriptionDelivery(Subscriptions subscriptions) : MessageHandler
{
    public override async Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        var envelope = context.Envelope!;
        // A local queue's address is local://{subscription, lower-cased}/.
        var subscription = subscriptions.Named(envelope.Destination!.Host);
        var operationContext = OperationContextHeaderExtensions.ContinuedOperationContext(envelope);
        var message = envelope.Message!;
        // Not the envelope's id: every subscription's envelope of one message has an id of its own.
        var identity = message is DomainEvent domainEvent ? domainEvent.Id : IntegrationEventIdentity(envelope);

        // Each attempt runs in a scope of its own: nothing from a failed attempt reaches the next.
        await using var scope = context.Runtime.Services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(operationContext, subscription.Module);
        var result = await scope
            .ServiceProvider.GetRequiredService<IDispatcher>()
            .SendAsync(new Deliver(subscription, message, identity), cancellation);
        if (result.IsFailure)
        {
            throw subscription.Failed(result.Errors);
        }
    }

    private static Guid IntegrationEventIdentity(Envelope envelope) =>
        envelope.Headers.TryGetValue(IntegrationEventHeaders.DomainEventId, out var value) && value is not null
            ? Guid.Parse(value, CultureInfo.InvariantCulture)
            : throw new InvalidOperationException(
                $"The Integration Event {envelope.Id} has no {IntegrationEventHeaders.DomainEventId} header, so its Receipt has no identity to record. Only the Unit of Work's Outbox sends Integration Events."
            );
}
