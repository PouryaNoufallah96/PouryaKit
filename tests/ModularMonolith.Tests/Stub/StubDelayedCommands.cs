using PouryaKit.Cqrs;
using PouryaKit.Domain;
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

internal sealed record WriteDelayedNoteCommand(string Text) : Command;

internal sealed class WriteDelayedNoteCommandHandler(IRepository<Note, NoteId> notes) : CommandHandler<WriteDelayedNoteCommand>
{
    public override async Task<Result> Handle(WriteDelayedNoteCommand command, CancellationToken cancellationToken)
    {
        await notes.AddAsync(Note.Write(command.Text), cancellationToken);
        return Result.Success();
    }
}
