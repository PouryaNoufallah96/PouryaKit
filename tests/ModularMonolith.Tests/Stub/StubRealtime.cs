using System.Collections.Concurrent;
using Microsoft.AspNetCore.Routing;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Realtime;
using PouryaKit.Web;

namespace ModularMonolith.Tests.Stub;

// Realtime pushes of notes, raised by WriteNoteRaisingCommand, and the Stub's SSE endpoint: every caller may listen on
// the notes, and a person on their own inbox only.

internal sealed record PushedNote(string Text);

internal static class StubChannels
{
    public const string Notes = "stub.notes";

    public static string InboxOf(string actor) => $"stub.inbox.{actor}";
}

internal sealed class StubRealtime : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapPouryaKitRealtime("/realtime", (actor, channel) => channel == StubChannels.Notes || channel == StubChannels.InboxOf(actor.Id));
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
