using Dapper;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

namespace ModularMonolith.Tests.Stub;

internal sealed record WriteTalliedNoteCommand(string Text, bool Reject) : Command;

// Writes a note through its repository and counts it in stub.note_tallies, a projection no EF Core model maps, with
// Dapper on the Unit of Work's connection and transaction.
internal sealed class WriteTalliedNoteCommandHandler(IRepository<Note, NoteId> notes, IUnitOfWorkConnection unitOfWork)
    : CommandHandler<WriteTalliedNoteCommand>
{
    public static readonly Error Rejected = Error.Conflict("Note.Rejected", "The note was rejected after it was tallied.");

    public override async Task<Result> Handle(WriteTalliedNoteCommand command, CancellationToken cancellationToken)
    {
        await notes.AddAsync(Note.Write(command.Text), cancellationToken);
        var (connection, transaction) = unitOfWork.Join();
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                insert into stub.note_tallies (text, count) values (@Text, 1)
                on conflict (text) do update set count = note_tallies.count + 1
                """,
                new { command.Text },
                transaction,
                cancellationToken: cancellationToken
            )
        );
        return command.Reject ? Rejected : Result.Success();
    }
}

internal sealed record JoinUnitOfWorkQuery : Query<bool>;

internal sealed class JoinUnitOfWorkQueryHandler(IUnitOfWorkConnection unitOfWork) : QueryHandler<JoinUnitOfWorkQuery, bool>
{
    public override Task<Result<bool>> Handle(JoinUnitOfWorkQuery query, CancellationToken cancellationToken)
    {
        unitOfWork.Join();
        return Task.FromResult<Result<bool>>(true);
    }
}
