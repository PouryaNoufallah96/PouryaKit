using Microsoft.EntityFrameworkCore;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

namespace ModularMonolith.Tests.Stub;

internal sealed record WriteNoteCommand(string Text) : Command;

internal sealed class WriteNoteCommandHandler(IRepository<Note, NoteId> notes) : CommandHandler<WriteNoteCommand>
{
    public override async Task<Result> Handle(WriteNoteCommand command, CancellationToken cancellationToken)
    {
        await notes.AddAsync(Note.Write(command.Text), cancellationToken);
        return Result.Success();
    }
}

internal enum Ending
{
    Fail,
    Throw,
}

internal sealed record WriteNoteThenFailCommand(string Text, Ending Ending) : Command;

// Feature code never touches its DbContext; this handler does, to put a row in the database inside the Operation's
// transaction before it fails, so only a rollback can remove it.
internal sealed class WriteNoteThenFailCommandHandler(IRepository<Note, NoteId> notes, StubDbContext dbContext)
    : CommandHandler<WriteNoteThenFailCommand>
{
    public static readonly Error Rejected = Error.Conflict("Note.Rejected", "The note was rejected after it was written.");

    public override async Task<Result> Handle(WriteNoteThenFailCommand command, CancellationToken cancellationToken)
    {
        await notes.AddAsync(Note.Write(command.Text), cancellationToken);
        await dbContext.Database.ExecuteSqlAsync(
            $"insert into stub.notes (text, version) values ({command.Text}, 0)",
            cancellationToken
        );
        return command.Ending == Ending.Throw ? throw new InvalidOperationException("The note handler threw.") : Rejected;
    }
}

internal sealed record ScopeProbe(StubDbContext DbContext, Operation Operation);

internal sealed record ProbeScopeQuery : Query<ScopeProbe>;

internal sealed class ProbeScopeQueryHandler(StubDbContext dbContext, Operation operation)
    : QueryHandler<ProbeScopeQuery, ScopeProbe>
{
    public override Task<Result<ScopeProbe>> Handle(ProbeScopeQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<Result<ScopeProbe>>(new ScopeProbe(dbContext, operation));
}

internal sealed record NotesFound(Note? ById, Note? First, List<Note> All, bool Any, int Count);

internal sealed record FindNotesQuery(long Id, string Text) : Query<NotesFound>;

internal sealed class FindNotesQueryHandler(IRepository<Note, NoteId> notes) : QueryHandler<FindNotesQuery, NotesFound>
{
    public override async Task<Result<NotesFound>> Handle(FindNotesQuery query, CancellationToken cancellationToken)
    {
        var withText = new NotesWithText(query.Text);
        return new NotesFound(
            await notes.FindAsync(new NoteId(query.Id), cancellationToken),
            await notes.FindAsync(withText, cancellationToken),
            await notes.ListAsync(withText, cancellationToken),
            await notes.AnyAsync(withText, cancellationToken),
            await notes.CountAsync(withText, cancellationToken)
        );
    }
}
