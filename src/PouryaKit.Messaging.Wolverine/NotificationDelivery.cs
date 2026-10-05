using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace PouryaKit.Messaging.Wolverine;

// The one pre-built handler of a Domain Event type, for every subscription to it: Wolverine routes one envelope to each
// subscription's own queue, so the queue an envelope arrived on names the subscription, and each is retried and
// acknowledged on its own. It generates no code, so the Domain Event and its handlers may be internal.
internal sealed class NotificationDelivery(NotificationSubscriptions subscriptions) : MessageHandler
{
    public override async Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        var envelope = context.Envelope!;
        // A local queue's address is local://{subscription, lower-cased}/.
        var subscription = subscriptions.Named(envelope.Destination!.Host);
        var operationContext = OperationContextHeaderExtensions.ContinuedOperationContext(envelope);

        // Each attempt runs in a scope of its own: nothing from a failed attempt reaches the next.
        await using var scope = context.Runtime.Services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(operationContext, subscription.Module);
        var result = await scope
            .ServiceProvider.GetRequiredService<IDispatcher>()
            .SendAsync(new DeliverNotification(subscription, (DomainEvent)envelope.Message!), cancellation);
        if (result.IsFailure)
        {
            throw new NotificationHandlerFailedException(subscription.Name, result.Errors);
        }
    }
}
