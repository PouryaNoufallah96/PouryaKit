using System.Collections.Concurrent;
using Dapper;
using Microsoft.EntityFrameworkCore;
using PouryaKit.Caching;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;
using PouryaKit.Persistence.Dapper;

namespace ModularMonolith.Tests.Stub;

/// <summary>What each run of a cached Stub query's handler answered, by the query's key, in any Operation.</summary>
internal sealed class CachedQueryRuns
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<int>> _answers = new();

    public void Add(string key, int answer) => _answers.GetOrAdd(key, _ => new()).Enqueue(answer);

    public IReadOnlyList<int> Of(string key) => _answers.TryGetValue(key, out var answers) ? [.. answers] : [];
}

// Counts the committed notes with a text. With a Hold, its handler reports that it runs, then waits to be let go.
internal sealed record CountNotesQuery(string Text, Hold? Hold = null) : Query<int>, ICacheableQuery
{
    public string CacheKey => Text;

    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);

    public IReadOnlyCollection<string> CacheTags => [$"notes:{Text}"];
}

internal sealed class CountNotesQueryHandler(IReadConnectionFactory database, CachedQueryRuns runs) : QueryHandler<CountNotesQuery, int>
{
    public override async Task<Result<int>> Handle(CountNotesQuery query, CancellationToken cancellationToken)
    {
        if (query.Hold is { } hold)
        {
            hold.Changed.TrySetResult();
            await hold.Released.Task.WaitAsync(cancellationToken);
        }

        await using var connection = await database.OpenAsync(cancellationToken);
        var count = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition("select count(*) from stub.notes where text = @Text", new { query.Text }, cancellationToken: cancellationToken)
        );
        runs.Add(query.Text, count);
        return count;
    }
}

// Reads a tally through the Module's DbContext, so inside a command's Unit of Work it sees that command's uncommitted
// tally, and outside one only committed tallies.
internal sealed record ReadTallyQuery(string Text) : Query<int>, ICacheableQuery
{
    public string CacheKey => Text;

    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);

    public IReadOnlyCollection<string> CacheTags => [$"tally:{Text}"];
}

internal sealed class ReadTallyQueryHandler(StubDbContext dbContext, CachedQueryRuns runs) : QueryHandler<ReadTallyQuery, int>
{
    public override async Task<Result<int>> Handle(ReadTallyQuery query, CancellationToken cancellationToken)
    {
        var tally = await dbContext
            .Database.SqlQuery<int>($"select coalesce(sum(count), 0)::int as \"Value\" from stub.note_tallies where text = {query.Text}")
            .SingleAsync(cancellationToken);
        runs.Add(query.Text, tally);
        return tally;
    }
}

// Tallies a note on the Unit of Work's connection, reads the tally back with a nested cached query, then fails, so the
// Unit of Work rolls the tally back.
internal sealed record TallyReadThenRefuseCommand(string Text) : Command;

internal sealed class TallyReadThenRefuseCommandHandler(IUnitOfWorkConnection unitOfWork, IDispatcher dispatcher)
    : CommandHandler<TallyReadThenRefuseCommand>
{
    public static readonly Error Refused = Error.Conflict("Note.TallyRefused", "The tally was refused after it was read.");

    public override async Task<Result> Handle(TallyReadThenRefuseCommand command, CancellationToken cancellationToken)
    {
        var (connection, transaction) = unitOfWork.Join();
        await connection.ExecuteAsync(
            new CommandDefinition(
                "insert into stub.note_tallies (text, count) values (@Text, 1)",
                new { command.Text },
                transaction,
                cancellationToken: cancellationToken
            )
        );
        await dispatcher.QueryAsync(new ReadTallyQuery(command.Text), cancellationToken);
        return Refused;
    }
}

// Answers whether a continuation its handler creates is trusted, which it is only inside its own running Operation.
internal sealed record ContinuesTrustedQuery(Guid Key) : Query<bool>, ICacheableQuery
{
    public string CacheKey => Key.ToString();

    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);

    public IReadOnlyCollection<string> CacheTags => [];
}

internal sealed class ContinuesTrustedQueryHandler(OperationContext context) : QueryHandler<ContinuesTrustedQuery, bool>
{
    public override Task<Result<bool>> Handle(ContinuesTrustedQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<Result<bool>>(context.Continue().IsTrustedContinuation);
}
