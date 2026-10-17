using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Modularity;
using PouryaKit.Realtime;
using PouryaKit.Web;

namespace ModularMonolith.Tests.Stub;

// Realtime pushes of notes, raised by WriteNoteRaisingCommand, and the Stub's SSE endpoint: every caller may listen on
// the notes, and a person on their own inbox only. Beside them, a command that tries to push from outside any
// Notification Handler's delivery, sent over HTTP or through the Gateway with Continue().

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
