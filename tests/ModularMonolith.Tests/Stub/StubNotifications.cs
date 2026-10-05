using System.Collections.Concurrent;
using Dapper;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Persistence;
using PouryaKit.Persistence.Dapper;

namespace ModularMonolith.Tests.Stub;

// Notification Handlers the Proving Sample must not hold: one that records what its delivery sees, one that keeps
// failing beside one that succeeds, and two that change the Stub's own data, the second only to fail afterwards.

// Writes a note raising each of Raised, in order, then fails if asked to, after the note is in the Unit of Work.
internal sealed record WriteNoteRaisingCommand(string Text, DomainEvent[] Raised, bool Fail = false) : Command;

internal sealed class WriteNoteRaisingCommandHandler(IRepository<Note, NoteId> notes)
    : CommandHandler<WriteNoteRaisingCommand>
{
    public static readonly Error Refused = Error.Conflict("Note.RefusedAfterRaising", "The note was refused after it raised.");

    public override async Task<Result> Handle(WriteNoteRaisingCommand command, CancellationToken cancellationToken)
    {
        var note = Note.Write(command.Text);
        foreach (var domainEvent in command.Raised)
        {
            note.Raising(domainEvent);
        }

        await notes.AddAsync(note, cancellationToken);
        return command.Fail ? Refused : Result.Success();
    }
}

/// <summary>What each delivery of a Stub Notification Handler saw, by the text of the note that raised it.</summary>
internal sealed class DeliveryLog
{
    private readonly ConcurrentQueue<Delivered> _entries = [];

    public IReadOnlyList<Delivered> For(string text) => [.. _entries.Where(entry => entry.Text == text)];

    public void Add(Delivered delivered) => _entries.Enqueue(delivered);
}

internal sealed record Delivered(string Handler, string Text, OperationContext Context, Operation Operation, bool NoteCommitted);

internal sealed record NotePostedDomainEvent(string Text) : DomainEvent;

internal sealed class RecordPostedNoteNotificationHandler(
    DeliveryLog log,
    OperationContext context,
    Operation operation,
    IReadConnectionFactory database
) : INotificationHandler<NotePostedDomainEvent>
{
    public async Task<Result> Handle(NotePostedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        // A connection of its own sees only committed rows.
        await using var connection = await database.OpenAsync(cancellationToken);
        var committed = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                "select count(*) from stub.notes where text = @Text",
                new { domainEvent.Text },
                cancellationToken: cancellationToken
            )
        );
        log.Add(new Delivered(nameof(RecordPostedNoteNotificationHandler), domainEvent.Text, context, operation, committed == 1));
        return Result.Success();
    }
}

internal sealed record NoteContestedDomainEvent(string Text) : DomainEvent;

internal sealed class AcceptContestedNoteNotificationHandler(DeliveryLog log, OperationContext context, Operation operation)
    : INotificationHandler<NoteContestedDomainEvent>
{
    public Task<Result> Handle(NoteContestedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        log.Add(new Delivered(nameof(AcceptContestedNoteNotificationHandler), domainEvent.Text, context, operation, true));
        return Task.FromResult(Result.Success());
    }
}

internal sealed class RejectContestedNoteNotificationHandler(DeliveryLog log, OperationContext context, Operation operation)
    : INotificationHandler<NoteContestedDomainEvent>
{
    public static readonly Error Rejected = Error.Conflict("Note.Contested", "The contested note is rejected, every time.");

    public Task<Result> Handle(NoteContestedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        log.Add(new Delivered(nameof(RejectContestedNoteNotificationHandler), domainEvent.Text, context, operation, true));
        return Task.FromResult<Result>(Rejected);
    }
}

// Its effect is a note written through the Stub's repository, which only the Stub's DbContext maps.
internal sealed record NoteFiledForCopyDomainEvent(string Text) : DomainEvent;

internal sealed class CopyFiledNoteNotificationHandler(IRepository<Note, NoteId> notes)
    : INotificationHandler<NoteFiledForCopyDomainEvent>
{
    public static string CopyOf(string text) => $"copy of {text}";

    public async Task<Result> Handle(NoteFiledForCopyDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        await notes.AddAsync(Note.Write(CopyOf(domainEvent.Text)), cancellationToken);
        return Result.Success();
    }
}

internal sealed record NoteFiledForBrokenCopyDomainEvent(string Text) : DomainEvent;

internal sealed class CopyThenRefuseNotificationHandler(IRepository<Note, NoteId> notes)
    : INotificationHandler<NoteFiledForBrokenCopyDomainEvent>
{
    public static readonly Error Refused = Error.Conflict("Note.CopyRefused", "The copy was refused after it was written.");

    public async Task<Result> Handle(NoteFiledForBrokenCopyDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        await notes.AddAsync(Note.Write(CopyFiledNoteNotificationHandler.CopyOf(domainEvent.Text)), cancellationToken);
        return Refused;
    }
}
