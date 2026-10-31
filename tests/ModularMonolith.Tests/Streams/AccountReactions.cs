using Catalog.Contracts.Products.Events.V1;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Persistence.Marten;

namespace ModularMonolith.Tests.Streams;

/// <summary>What the Accounts Module publishes when an account is opened, and receives itself.</summary>
public sealed record AccountOpenedV1(Guid AccountId, Guid Customer);

// The welcome: its Stream effect, its Integration Event and its Receipt commit together or not at all.
[SubscriptionName("accounts.welcome")]
internal sealed class WelcomeAccountHolder(IStreams streams, IIntegrationEventPublisher publisher)
    : INotificationHandler<AccountOpened>
{
    public async Task<Result> Handle(AccountOpened domainEvent, CancellationToken cancellationToken)
    {
        AccountProbe.Ran(domainEvent.AccountId);
        var account = (
            await streams.LoadAsync<Account>(
                new AccountId(domainEvent.AccountId),
                cancellationToken
            )
        )!;
        account.Welcome();
        publisher.Publish(
            domainEvent.Id,
            new AccountOpenedV1(domainEvent.AccountId, domainEvent.Customer.Value)
        );
        await AccountProbe.HoldAsync(domainEvent.AccountId, cancellationToken);
        return AccountProbe.Fails(domainEvent.AccountId)
            ? Error.Unexpected("Accounts.WelcomeFailed", "The welcome failed.")
            : Result.Success();
    }
}

// Gives Deposited a subscription that writes nothing: its delivery's Receipt is the only write.
[SubscriptionName("accounts.note-deposit")]
internal sealed class NoteDeposit : INotificationHandler<Deposited>
{
    public Task<Result> Handle(Deposited domainEvent, CancellationToken cancellationToken)
    {
        AccountProbe.Ran(domainEvent.Id);
        return Task.FromResult(Result.Success());
    }
}

internal sealed class NoteAccountOpened : IIntegrationEventHandler<AccountOpenedV1>
{
    public Task<Result> Handle(
        AccountOpenedV1 integrationEvent,
        CancellationToken cancellationToken
    ) => Task.FromResult(Result.Success());
}

// Catalog's Snapshots, in a Host that has Catalog: applied only when newer, through the version kept in this Module's table.
internal sealed class NoteProductSnapshot : IIntegrationEventHandler<ProductSnapshotV1>
{
    public Task<Result> Handle(ProductSnapshotV1 snapshot, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}
