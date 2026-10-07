using System.Collections.Concurrent;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

namespace ModularMonolith.Tests.Stub;

internal sealed record RunId(Guid Value) : TypedId<Guid>(Value);

/// <summary>What one run of <see cref="CountNoteCommand"/> answered: a value no other run answers.</summary>
internal sealed record CountedNote(RunId Run, int Number, string Text);

// Writes a note whose Domain Event a Notification Handler subscribes to, so every run changes data and stores a
// Notification, and an unguarded repeat succeeds again with another value. With a Hold it waits after its change until
// released; with Fail it then fails, so only a rollback removes what it wrote.
[Idempotent]
internal sealed record CountNoteCommand(string Text, Hold? Hold = null, bool Fail = false) : Command<CountedNote>;

internal sealed class CountNoteCommandHandler(IRepository<Note, NoteId> notes, NoteRuns runs)
    : CommandHandler<CountNoteCommand, CountedNote>
{
    public static readonly Error Refused = Error.Conflict("Note.CountRefused", "The counted note was refused after it was written.");

    public override async Task<Result<CountedNote>> Handle(CountNoteCommand command, CancellationToken cancellationToken)
    {
        var number = runs.Next(command.Text);
        await notes.AddAsync(Note.Write(command.Text).Raising(new NotePostedDomainEvent(command.Text)), cancellationToken);
        if (command.Hold is { } hold)
        {
            hold.Changed.SetResult();
            await hold.Released.Task.WaitAsync(cancellationToken);
        }

        return command.Fail ? Refused : new CountedNote(new RunId(Guid.CreateVersion7()), number, command.Text);
    }
}

/// <summary>How many times the handler of <see cref="CountNoteCommand"/> ran, by the note's text, in any Operation.</summary>
internal sealed class NoteRuns
{
    private readonly ConcurrentDictionary<string, int> _runs = new();

    public int Next(string text) => _runs.AddOrUpdate(text, 1, (_, runs) => runs + 1);

    public int Of(string text) => _runs.GetValueOrDefault(text);
}
