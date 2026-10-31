using System.Collections.Concurrent;
using Catalog.Contracts.Products.Events.V1;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Persistence;

namespace ModularMonolith.Tests.Stub;

// Integration Events the Proving Sample must not hold: the Stub publishes them and subscribes to them itself, with one
// handler that records what its delivery sees, one that fails after its effect, and Notification Handlers that publish
// and then fail, publish what they must not, or publish what the wire options cannot write.

internal sealed record NoteSharedV1(Guid Id, DateTimeOffset OccurredOn, string Text);

internal sealed record NoteCopyRequestedV1(Guid Id, DateTimeOffset OccurredOn, string Text);

/// <summary>What each delivery of a Stub Integration Event Handler received and ran as.</summary>
internal sealed class IntegrationEventLog
{
    private readonly ConcurrentQueue<Received> _entries = [];

    public IReadOnlyList<Received> All => [.. _entries];

    public void Add(Received received) => _entries.Enqueue(received);
}

internal sealed record Received(
    object IntegrationEvent,
    OperationContext Context,
    Operation Operation
);

internal sealed record NoteToShareDomainEvent(string Text, bool RefuseAfterSharing = false)
    : DomainEvent;

internal sealed class ShareNoteNotificationHandler(
    IIntegrationEventPublisher events,
    DeliveryLog log,
    OperationContext context,
    Operation operation
) : INotificationHandler<NoteToShareDomainEvent>
{
    public static readonly Error Refused = Error.Conflict(
        "Note.ShareRefused",
        "The note was refused after it was shared."
    );

    public Task<Result> Handle(
        NoteToShareDomainEvent domainEvent,
        CancellationToken cancellationToken
    )
    {
        log.Add(
            new Delivered(
                nameof(ShareNoteNotificationHandler),
                domainEvent.Text,
                context,
                operation,
                true
            )
        );
        events.Publish(
            domainEvent.Id,
            new NoteSharedV1(domainEvent.Id, domainEvent.OccurredOn, domainEvent.Text)
        );
        return Task.FromResult(domainEvent.RefuseAfterSharing ? Refused : Result.Success());
    }
}

internal sealed class RecordSharedNoteIntegrationEventHandler(
    IntegrationEventLog log,
    OperationContext context,
    Operation operation
) : IIntegrationEventHandler<NoteSharedV1>
{
    public Task<Result> Handle(NoteSharedV1 integrationEvent, CancellationToken cancellationToken)
    {
        log.Add(new Received(integrationEvent, context, operation));
        return Task.FromResult(Result.Success());
    }
}

// Writes a copy of the note, then publishes it shared: without its text when asked, a null the contract forbids, which
// the strict wire options refuse to write.
internal sealed record NoteToCopyAndShareDomainEvent(string Text, bool WithoutText) : DomainEvent;

internal sealed class CopyAndShareNoteNotificationHandler(
    IRepository<Note, NoteId> notes,
    IIntegrationEventPublisher events
) : INotificationHandler<NoteToCopyAndShareDomainEvent>
{
    public async Task<Result> Handle(
        NoteToCopyAndShareDomainEvent domainEvent,
        CancellationToken cancellationToken
    )
    {
        await notes.AddAsync(
            Note.Write(CopyFiledNoteNotificationHandler.CopyOf(domainEvent.Text)),
            cancellationToken
        );
        events.Publish(
            domainEvent.Id,
            new NoteSharedV1(
                domainEvent.Id,
                domainEvent.OccurredOn,
                domainEvent.WithoutText ? null! : domainEvent.Text
            )
        );
        return Result.Success();
    }
}

internal sealed record NoteCopyRequestedDomainEvent(string Text) : DomainEvent;

internal sealed class RequestNoteCopyNotificationHandler(IIntegrationEventPublisher events)
    : INotificationHandler<NoteCopyRequestedDomainEvent>
{
    public Task<Result> Handle(
        NoteCopyRequestedDomainEvent domainEvent,
        CancellationToken cancellationToken
    )
    {
        events.Publish(
            domainEvent.Id,
            new NoteCopyRequestedV1(domainEvent.Id, domainEvent.OccurredOn, domainEvent.Text)
        );
        return Task.FromResult(Result.Success());
    }
}

// Its effect is a note written through the Stub's repository; then it fails, every time.
internal sealed class CopyRequestedNoteThenRefuseIntegrationEventHandler(
    IRepository<Note, NoteId> notes,
    IntegrationEventLog log,
    OperationContext context,
    Operation operation
) : IIntegrationEventHandler<NoteCopyRequestedV1>
{
    public static readonly Error Refused = Error.Conflict(
        "Note.CopyRequestRefused",
        "The requested copy was refused after it was written."
    );

    public async Task<Result> Handle(
        NoteCopyRequestedV1 integrationEvent,
        CancellationToken cancellationToken
    )
    {
        log.Add(new Received(integrationEvent, context, operation));
        await notes.AddAsync(
            Note.Write(CopyFiledNoteNotificationHandler.CopyOf(integrationEvent.Text)),
            cancellationToken
        );
        return Refused;
    }
}

internal enum Misuse
{
    AnotherIdentity,
    UndeclaredType,
    AnotherModulesType,
    SameTypeTwice,
}

internal sealed record UndeclaredV1(Guid Id);

// Publishes the way a Notification Handler must not.
internal sealed record PublishingMisusedDomainEvent(Misuse Misuse) : DomainEvent;

internal sealed class MisusePublisherNotificationHandler(IIntegrationEventPublisher events)
    : INotificationHandler<PublishingMisusedDomainEvent>
{
    public Task<Result> Handle(
        PublishingMisusedDomainEvent domainEvent,
        CancellationToken cancellationToken
    )
    {
        switch (domainEvent.Misuse)
        {
            case Misuse.AnotherIdentity:
                events.Publish(
                    Guid.CreateVersion7(),
                    new NoteSharedV1(domainEvent.Id, domainEvent.OccurredOn, "misused")
                );
                break;
            case Misuse.UndeclaredType:
                events.Publish(domainEvent.Id, new UndeclaredV1(domainEvent.Id));
                break;
            case Misuse.SameTypeTwice:
                events.Publish(
                    domainEvent.Id,
                    new NoteSharedV1(domainEvent.Id, domainEvent.OccurredOn, "misused")
                );
                events.Publish(
                    domainEvent.Id,
                    new NoteSharedV1(domainEvent.Id, domainEvent.OccurredOn, "misused again")
                );
                break;
            default:
                events.Publish(
                    domainEvent.Id,
                    new ProductCreatedV1(
                        domainEvent.Id,
                        domainEvent.OccurredOn,
                        Guid.CreateVersion7(),
                        "misused"
                    )
                );
                break;
        }

        return Task.FromResult(Result.Success());
    }
}

// A command is not where an Integration Event is published.
internal sealed record PublishFromCommandCommand(string Text) : Command;

internal sealed class PublishFromCommandCommandHandler(IIntegrationEventPublisher events)
    : CommandHandler<PublishFromCommandCommand>
{
    public override Task<Result> Handle(
        PublishFromCommandCommand command,
        CancellationToken cancellationToken
    )
    {
        events.Publish(
            Guid.CreateVersion7(),
            new NoteSharedV1(Guid.CreateVersion7(), DateTimeOffset.UtcNow, command.Text)
        );
        return Task.FromResult(Result.Success());
    }
}

// Two Notification Handlers of one Domain Event that each publish the same type: one Integration Event too many.
internal sealed record NoteToShareTwiceDomainEvent(string Text) : DomainEvent;

internal sealed class FirstShareTwiceNotificationHandler(IIntegrationEventPublisher events)
    : INotificationHandler<NoteToShareTwiceDomainEvent>
{
    public Task<Result> Handle(
        NoteToShareTwiceDomainEvent domainEvent,
        CancellationToken cancellationToken
    )
    {
        events.Publish(
            domainEvent.Id,
            new NoteSharedV1(domainEvent.Id, domainEvent.OccurredOn, domainEvent.Text)
        );
        return Task.FromResult(Result.Success());
    }
}

internal sealed class SecondShareTwiceNotificationHandler(IIntegrationEventPublisher events)
    : INotificationHandler<NoteToShareTwiceDomainEvent>
{
    public Task<Result> Handle(
        NoteToShareTwiceDomainEvent domainEvent,
        CancellationToken cancellationToken
    )
    {
        events.Publish(
            domainEvent.Id,
            new NoteSharedV1(domainEvent.Id, domainEvent.OccurredOn, domainEvent.Text)
        );
        return Task.FromResult(Result.Success());
    }
}

// A Notification Handler whose nested query publishes, then fails; the handler ignores the query's Result.
internal sealed record NoteToShareThroughQueryDomainEvent(string Text) : DomainEvent;

internal sealed record PublishInQueryQuery(
    Guid DomainEventId,
    DateTimeOffset OccurredOn,
    string Text
) : Query<bool>;

internal sealed class PublishInQueryQueryHandler(IIntegrationEventPublisher events)
    : QueryHandler<PublishInQueryQuery, bool>
{
    public static readonly Error Failed = Error.Unexpected(
        "Note.QueryPublished",
        "The query failed after it published."
    );

    public override Task<Result<bool>> Handle(
        PublishInQueryQuery query,
        CancellationToken cancellationToken
    )
    {
        events.Publish(
            query.DomainEventId,
            new NoteSharedV1(query.DomainEventId, query.OccurredOn, query.Text)
        );
        return Task.FromResult<Result<bool>>(Failed);
    }
}

internal sealed class ShareThroughQueryNotificationHandler(IDispatcher dispatcher)
    : INotificationHandler<NoteToShareThroughQueryDomainEvent>
{
    public async Task<Result> Handle(
        NoteToShareThroughQueryDomainEvent domainEvent,
        CancellationToken cancellationToken
    )
    {
        await dispatcher.QueryAsync(
            new PublishInQueryQuery(domainEvent.Id, domainEvent.OccurredOn, domainEvent.Text),
            cancellationToken
        );
        return Result.Success();
    }
}
