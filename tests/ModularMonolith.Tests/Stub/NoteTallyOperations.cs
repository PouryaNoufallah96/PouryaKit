using Dapper;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

namespace ModularMonolith.Tests.Stub;

internal sealed record WriteTalliedNoteCommand(string Text, bool Reject) : Command;

// Writes a note through its repository and counts it in stub.note_tallies, a projection no EF Core model maps, with
// Dapper on the Unit of Work's connection and transaction.
internal sealed class WriteTalliedNoteCommandHandler(
    IRepository<Note, NoteId> notes,
    IUnitOfWorkConnection unitOfWork
) : CommandHandler<WriteTalliedNoteCommand>
{
    public static readonly Error Rejected = Error.Conflict(
        "Note.Rejected",
        "The note was rejected after it was tallied."
    );

    public override async Task<Result> Handle(
        WriteTalliedNoteCommand command,
        CancellationToken cancellationToken
    )
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

internal sealed record TallyNoteInQueryQuery(string Text) : Query<bool>;

// A query that tallies through the Unit of Work and then fails: a failed query marks nothing rollback-only, so the
// tally would commit with the command that sent it if the query could write.
internal sealed class TallyNoteInQueryQueryHandler(IUnitOfWorkConnection unitOfWork)
    : QueryHandler<TallyNoteInQueryQuery, bool>
{
    public static readonly Error Failed = Error.Unexpected(
        "Note.TallyFailed",
        "The query failed after it tallied."
    );

    public override async Task<Result<bool>> Handle(
        TallyNoteInQueryQuery query,
        CancellationToken cancellationToken
    )
    {
        var (connection, transaction) = unitOfWork.Join();
        await connection.ExecuteAsync(
            new CommandDefinition(
                "insert into stub.note_tallies (text, count) values (@Text, 1)",
                new { query.Text },
                transaction,
                cancellationToken: cancellationToken
            )
        );
        return Failed;
    }
}

internal sealed record TallyNoteThroughQueryCommand(string Text) : Command;

// Ignores the nested query's Result, as a careless handler would.
internal sealed class TallyNoteThroughQueryCommandHandler(IDispatcher dispatcher)
    : CommandHandler<TallyNoteThroughQueryCommand>
{
    public override async Task<Result> Handle(
        TallyNoteThroughQueryCommand command,
        CancellationToken cancellationToken
    )
    {
        await dispatcher.QueryAsync(new TallyNoteInQueryQuery(command.Text), cancellationToken);
        return Result.Success();
    }
}
