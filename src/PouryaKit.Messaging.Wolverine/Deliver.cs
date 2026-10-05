using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Messaging.Wolverine;

// One delivery attempt, run as the Operation of the scope the delivery step opened: through the dispatcher, so it gets
// the subscribing Module's Unit of Work, the Operation's clock, and everything a command gets, and a nested command or
// a Domain Event the handler causes joins it. MessageId is the Notification's or Integration Event's identity, the same
// in every delivery of it.
internal sealed record Deliver(Subscription Subscription, object Message, Guid MessageId) : Command;

internal sealed class DeliverHandler(IServiceProvider scope, IntegrationEventPublisher publisher, IReceipts? receipts = null)
    : CommandHandler<Deliver>
{
    public override async Task<Result> Handle(Deliver command, CancellationToken cancellationToken)
    {
        // Recorded before the handler runs, in the same transaction: a redelivery after the commit finds it and does
        // nothing, and a concurrent one waits for this one to commit or roll back.
        if (
            receipts is not null
            && !await receipts.RecordAsync(command.Subscription.Name, command.MessageId, cancellationToken)
        )
        {
            return Result.Success();
        }

        // What a Notification Handler publishes follows from the Domain Event it handles.
        publisher.Following = command.Message as DomainEvent;
        return await command.Subscription.InvokeAsync(scope, command.Message, cancellationToken);
    }
}
