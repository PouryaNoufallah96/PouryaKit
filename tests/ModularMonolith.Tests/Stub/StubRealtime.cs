using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Modularity;
using PouryaKit.Persistence;
using PouryaKit.Realtime;
using PouryaKit.Web;

namespace ModularMonolith.Tests.Stub;

// Realtime pushes of notes, raised by WriteNoteRaisingCommand, and the Stub's SSE endpoint: every caller may listen on
// the notes, and a person on their own inbox only. Beside them, commands that try to push from outside any
// Notification Handler's delivery: sent over HTTP, through the Gateway with Continue(), or through the Gateway by a
// Notification Handler with its delivery's own context; and a Domain Event Handler of a Notification Handler's change.

internal sealed record PushedNote(string Text);

internal static class StubChannels
{
    public const string Notes = "stub.notes";

    public static string InboxOf(string actor) => $"stub.inbox.{actor}";
}

internal sealed class StubRealtime : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPouryaKitRealtime("/realtime", (actor, channel) => channel == StubChannels.Notes || channel == StubChannels.InboxOf(actor.Id));
        routes.MapPost(
            "/realtime/notes/{text}",
            async (string text, OperationContext context, IGatewayProcessor<StubModule> stub, CancellationToken cancellationToken) =>
                (await stub.SendAsync(new PushNoteCommand(text), context, cancellationToken)).ToHttpResult()
        );
    }
}

// Records, by text, whether the command ran as a trusted continuation and what the publisher threw at its push.
internal sealed record PushNoteCommand(string Text) : Command;

internal sealed class PushNoteCommandHandler(IRealtimePublisher realtime, OperationContext context) : CommandHandler<PushNoteCommand>
{
    public static readonly ConcurrentDictionary<string, (bool Trusted, string? Refusal)> Pushes = [];

    public override async Task<Result> Handle(PushNoteCommand command, CancellationToken cancellationToken)
    {
        try
        {
            await realtime.PublishAsync(StubChannels.Notes, new PushedNote(command.Text), cancellationToken);
            Pushes[command.Text] = (context.IsTrustedContinuation, null);
        }
        catch (InvalidOperationException refused)
        {
            Pushes[command.Text] = (context.IsTrustedContinuation, refused.Message);
        }

        return Result.Success();
    }
}

// Sends PushNoteCommand through the Stub's Gateway as a trusted continuation of its own Operation.
internal sealed record RelayPushNoteCommand(string Text) : Command;

internal sealed class RelayPushNoteCommandHandler(IGatewayProcessor<StubModule> stub, OperationContext context)
    : CommandHandler<RelayPushNoteCommand>
{
    public override Task<Result> Handle(RelayPushNoteCommand command, CancellationToken cancellationToken) =>
        stub.SendAsync(new PushNoteCommand(command.Text), context.Continue(), cancellationToken);
}

internal sealed record NotePushedDomainEvent(string Channel, string Text) : DomainEvent;

// Records what each push returned, by the Domain Event's identity, and succeeds whatever it was, so a refused push is
// neither retried nor set aside.
internal sealed class PushNoteNotificationHandler(IRealtimePublisher realtime) : INotificationHandler<NotePushedDomainEvent>
{
    public static readonly ConcurrentDictionary<Guid, Result> Pushed = [];

    public async Task<Result> Handle(NotePushedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        Pushed[domainEvent.Id] = await realtime.PublishAsync(domainEvent.Channel, new PushedNote(domainEvent.Text), cancellationToken);
        return Result.Success();
    }
}

internal sealed record NotePushedThenFailingDomainEvent(string Text) : DomainEvent;

// Pushes "<text> 1" and then fails its first delivery, which rolls back; its retry pushes "<text> 2" and commits.
internal sealed class PushNoteThenFailOnceNotificationHandler(IRealtimePublisher realtime)
    : INotificationHandler<NotePushedThenFailingDomainEvent>
{
    public static readonly Error FailedAfterPushing = Error.Conflict("Note.FailedAfterPushing", "The delivery failed after its push, once.");

    // Process-wide, so the Host that delivers counts whatever Host the test composed.
    private static readonly ConcurrentDictionary<string, int> Attempts = [];

    public async Task<Result> Handle(NotePushedThenFailingDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        var attempt = Attempts.AddOrUpdate(domainEvent.Text, 1, (_, attempts) => attempts + 1);
        var pushed = await realtime.PublishAsync(StubChannels.Notes, new PushedNote($"{domainEvent.Text} {attempt}"), cancellationToken);
        return pushed.IsFailure || attempt > 1 ? pushed : FailedAfterPushing;
    }
}

internal sealed record NoteRelayedThenFailingDomainEvent(string Text) : DomainEvent;

// Sends PushAndShareNoteCommand through the Stub's Gateway with its delivery's own context, as it was injected, then
// fails, so the delivery rolls back. The Gateway is resolved when it runs: the test Hosts without Modules that register
// the Stub's Notification Handlers have none.
internal sealed class RelayNoteThenFailNotificationHandler(IServiceProvider services, OperationContext context)
    : INotificationHandler<NoteRelayedThenFailingDomainEvent>
{
    public static readonly Error FailedAfterRelaying = Error.Conflict("Note.FailedAfterRelaying", "The delivery failed after its relay.");

    public async Task<Result> Handle(NoteRelayedThenFailingDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        await services
            .GetRequiredService<IGatewayProcessor<StubModule>>()
            .SendAsync(new PushAndShareNoteCommand(domainEvent.Text, domainEvent.Id, domainEvent.OccurredOn), context, cancellationToken);
        return FailedAfterRelaying;
    }
}

// Pushes the note and publishes it shared with the identity of the Domain Event named, and records, by text, what each
// publisher threw.
internal sealed record PushAndShareNoteCommand(string Text, Guid DomainEventId, DateTimeOffset OccurredOn) : Command;

internal sealed class PushAndShareNoteCommandHandler(IRealtimePublisher realtime, IIntegrationEventPublisher events)
    : CommandHandler<PushAndShareNoteCommand>
{
    public static readonly ConcurrentDictionary<string, (string? Unpushed, string? Unpublished)> Refusals = [];

    public override async Task<Result> Handle(PushAndShareNoteCommand command, CancellationToken cancellationToken)
    {
        string? unpushed = null;
        string? unpublished = null;
        try
        {
            await realtime.PublishAsync(StubChannels.Notes, new PushedNote(command.Text), cancellationToken);
        }
        catch (InvalidOperationException refused)
        {
            unpushed = refused.Message;
        }

        try
        {
            events.Publish(command.DomainEventId, new NoteSharedV1(command.DomainEventId, command.OccurredOn, command.Text));
        }
        catch (InvalidOperationException refused)
        {
            unpublished = refused.Message;
        }

        Refusals[command.Text] = (unpushed, unpublished);
        return Result.Success();
    }
}

internal sealed record NoteHandedOnDomainEvent(string Text) : DomainEvent;

// Pushes the note, then writes one in its delivery's Unit of Work that raises NoteWrittenInDeliveryDomainEvent, whose
// Domain Event Handler the Unit of Work runs in the same scope once this returns. The repository is resolved when it
// runs: the test Hosts without Modules that register the Stub's Notification Handlers have none.
internal sealed class PushThenWriteNoteNotificationHandler(IRealtimePublisher realtime, IServiceProvider services)
    : INotificationHandler<NoteHandedOnDomainEvent>
{
    public async Task<Result> Handle(NoteHandedOnDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        var pushed = await realtime.PublishAsync(StubChannels.Notes, new PushedNote(domainEvent.Text), cancellationToken);
        if (pushed.IsFailure)
        {
            return pushed;
        }

        await services
            .GetRequiredService<IRepository<Note, NoteId>>()
            .AddAsync(Note.Write(domainEvent.Text).Raising(new NoteWrittenInDeliveryDomainEvent(domainEvent.Text, domainEvent.Id, domainEvent.OccurredOn)), cancellationToken);
        return Result.Success();
    }
}

internal sealed record NoteWrittenInDeliveryDomainEvent(string Text, Guid DeliveredId, DateTimeOffset DeliveredOn) : DomainEvent;

// Pushes the note again and publishes it shared with the delivered Domain Event's identity, and records, by text, what
// each publisher threw.
internal sealed class PushAndShareWrittenNoteReaction(IRealtimePublisher realtime, IIntegrationEventPublisher events)
    : IDomainEventHandler<NoteWrittenInDeliveryDomainEvent>
{
    public static readonly ConcurrentDictionary<string, (string? Unpushed, string? Unpublished)> Refusals = [];

    public async Task<Result> Handle(NoteWrittenInDeliveryDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        string? unpushed = null;
        string? unpublished = null;
        try
        {
            await realtime.PublishAsync(StubChannels.Notes, new PushedNote($"{domainEvent.Text} again"), cancellationToken);
        }
        catch (InvalidOperationException refused)
        {
            unpushed = refused.Message;
        }

        try
        {
            events.Publish(domainEvent.DeliveredId, new NoteSharedV1(domainEvent.DeliveredId, domainEvent.DeliveredOn, domainEvent.Text));
        }
        catch (InvalidOperationException refused)
        {
            unpublished = refused.Message;
        }

        Refusals[domainEvent.Text] = (unpushed, unpublished);
        return Result.Success();
    }
}
