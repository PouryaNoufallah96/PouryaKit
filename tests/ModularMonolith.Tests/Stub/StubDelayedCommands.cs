using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Persistence;

namespace ModularMonolith.Tests.Stub;

public enum SchedulingFailure
{
    Result,
    Nested,
    Undeclared,
}

// Writes a note, schedules a delayed note for one second later, then fails as asked: with a failed Result, by ignoring
// a nested command that failed, or by scheduling a command the Stub never declared.
internal sealed record ScheduleNoteThenFailCommand(string Text, SchedulingFailure Failure) : Command;

internal sealed class ScheduleNoteThenFailCommandHandler(
    IRepository<Note, NoteId> notes,
    IDelayedCommands later,
    IDispatcher dispatcher,
    TimeProvider time
) : CommandHandler<ScheduleNoteThenFailCommand>
{
    public static readonly Error Refused = Error.Conflict("Note.SchedulingRefused", "The note was refused after it scheduled another.");

    public override async Task<Result> Handle(ScheduleNoteThenFailCommand command, CancellationToken cancellationToken)
    {
        await notes.AddAsync(Note.Write(command.Text), cancellationToken);
        later.Schedule(
            command.Failure == SchedulingFailure.Undeclared
                ? new WriteNoteCommand(command.Text)
                : new WriteDelayedNoteCommand($"delayed {command.Text}"),
            time.GetUtcNow().AddSeconds(1)
        );
        if (command.Failure == SchedulingFailure.Result)
        {
            return Refused;
        }

        _ = await dispatcher.SendAsync(new NestedNoteCommand(command.Text, NestedFailure.Handler), cancellationToken); // ignored on purpose
        return Result.Success();
    }
}

// Schedules a note for two seconds later under the name stub.kind-changed, which only a test's own Host declares: as a
// delayed command in the Host that schedules it, and as a recurring command in the Host that finds it stored.
internal sealed record ScheduleKindChangedNoteCommand(string Text) : Command;

internal sealed class ScheduleKindChangedNoteCommandHandler(IDelayedCommands later, TimeProvider time)
    : CommandHandler<ScheduleKindChangedNoteCommand>
{
    public override Task<Result> Handle(ScheduleKindChangedNoteCommand command, CancellationToken cancellationToken)
    {
        later.Schedule(new WriteKindChangedNoteCommand(command.Text), time.GetUtcNow().AddSeconds(2));
        return Task.FromResult(Result.Success());
    }
}

internal sealed record WriteKindChangedNoteCommand(string Text) : Command;

internal sealed class WriteKindChangedNoteCommandHandler(IRepository<Note, NoteId> notes) : CommandHandler<WriteKindChangedNoteCommand>
{
    public override async Task<Result> Handle(WriteKindChangedNoteCommand command, CancellationToken cancellationToken)
    {
        await notes.AddAsync(Note.Write(command.Text), cancellationToken);
        return Result.Success();
    }
}

// Writes its note when it runs and, given a Then, schedules a note of that text for an hour later, from its own run.
internal sealed record WriteDelayedNoteCommand(string Text, string? Then = null) : Command;

internal sealed class WriteDelayedNoteCommandHandler(IRepository<Note, NoteId> notes, IDelayedCommands later, TimeProvider time)
    : CommandHandler<WriteDelayedNoteCommand>
{
    public override async Task<Result> Handle(WriteDelayedNoteCommand command, CancellationToken cancellationToken)
    {
        await notes.AddAsync(Note.Write(command.Text), cancellationToken);
        if (command.Then is { } then)
        {
            later.Schedule(new WriteDelayedNoteCommand(then), time.GetUtcNow().AddHours(1));
        }

        return Result.Success();
    }
}

// Raised to have a note's reactions schedule: its Domain Event Handler in the Operation that wrote the note, an hour
// ahead, and its Notification Handler in its delivery, at once, a note whose own run schedules one more.
internal sealed record NoteToFollowUpDomainEvent(string Text) : DomainEvent;

internal sealed class ScheduleFollowUpReaction(IDelayedCommands later, TimeProvider time) : IDomainEventHandler<NoteToFollowUpDomainEvent>
{
    public Task<Result> Handle(NoteToFollowUpDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        later.Schedule(new WriteDelayedNoteCommand(TextOf(domainEvent.Text)), time.GetUtcNow().AddHours(1));
        return Task.FromResult(Result.Success());
    }

    public static string TextOf(string text) => $"from the Domain Event Handler of {text}";
}

internal sealed class ScheduleFollowUpNotificationHandler(IDelayedCommands later, TimeProvider time)
    : INotificationHandler<NoteToFollowUpDomainEvent>
{
    public Task<Result> Handle(NoteToFollowUpDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        later.Schedule(new WriteDelayedNoteCommand(TextOf(domainEvent.Text), Then: RunTextOf(domainEvent.Text)), time.GetUtcNow());
        return Task.FromResult(Result.Success());
    }

    public static string TextOf(string text) => $"from the Notification Handler of {text}";

    public static string RunTextOf(string text) => $"from the delayed run of {text}";
}
