using Ledger.Accounts.Entities;
using Ledger.Accounts.Events.DomainEvents;
using Ledger.Contracts.Accounts.Events.V1;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Persistence.Marten;

namespace Ledger.Accounts.Events.Notifications;

// Activates each new account once its opening is committed, and tells other Modules about it: the activation, the
// Integration Event and the delivery's Receipt commit together, or none does. The Integration Event carries the Domain
// Event's identity and time, so every receiver recognises a redelivery. An entry posted to the account meanwhile appends
// to the same stream, so one of the two is the Kit's concurrency conflict, and a conflicting delivery is retried.
internal sealed class ActivateAccountNotificationHandler(
    IStreams streams,
    IIntegrationEventPublisher events
) : INotificationHandler<AccountOpenedDomainEvent>
{
    public async Task<Result> Handle(
        AccountOpenedDomainEvent domainEvent,
        CancellationToken cancellationToken
    )
    {
        var account = (await streams.LoadAsync<Account>(domainEvent.AccountId, cancellationToken))!;
        account.Activate();
        events.Publish(
            domainEvent.Id,
            new AccountOpenedV1(
                domainEvent.Id,
                domainEvent.OccurredOn,
                domainEvent.AccountId.Value,
                domainEvent.Holder
            )
        );
        return Result.Success();
    }
}
