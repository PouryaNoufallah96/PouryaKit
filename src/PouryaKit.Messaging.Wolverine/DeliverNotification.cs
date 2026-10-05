using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Messaging.Wolverine;

// One delivery attempt, run as the Operation of the scope the delivery step opened: through the dispatcher, so it gets
// the subscribing Module's Unit of Work, the Operation's clock, and everything a command gets, and a nested command or
// a Domain Event the handler causes joins it.
internal sealed record DeliverNotification(NotificationSubscription Subscription, DomainEvent DomainEvent) : Command;

internal sealed class DeliverNotificationHandler(IServiceProvider scope, IReceipts? receipts = null)
    : CommandHandler<DeliverNotification>
{
    public override async Task<Result> Handle(DeliverNotification command, CancellationToken cancellationToken)
    {
        // Recorded before the handler runs, in the same transaction: a redelivery after the commit finds it and does
        // nothing, and a concurrent one waits for this one to commit or roll back.
        if (
            receipts is not null
            && !await receipts.RecordAsync(command.Subscription.Name, command.DomainEvent.Id, cancellationToken)
        )
        {
            return Result.Success();
        }

        return await command.Subscription.InvokeAsync(scope, command.DomainEvent, cancellationToken);
    }
}
