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

/// <summary>What <see cref="HeldTallyQuery"/> answered: the committed tally, and whether its handler's continuation was trusted.</summary>
internal sealed record HeldTally(int Tally, bool Trusted);

// Reads a tally through the Module's DbContext, then continues its Operation. With a Hold, its handler reports that it
// runs, then waits to be let go before it reads.
internal sealed record HeldTallyQuery(string Text, Hold? Hold = null) : Query<HeldTally>, ICacheableQuery
{
    public string CacheKey => Text;

    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);

    public IReadOnlyCollection<string> CacheTags => [$"tally:{Text}"];
}

internal sealed class HeldTallyQueryHandler(StubDbContext dbContext, OperationContext context, CachedQueryRuns runs)
    : QueryHandler<HeldTallyQuery, HeldTally>
{
    public override async Task<Result<HeldTally>> Handle(HeldTallyQuery query, CancellationToken cancellationToken)
    {
        if (query.Hold is { } hold)
        {
            hold.Changed.TrySetResult();
            await hold.Released.Task.WaitAsync(cancellationToken);
        }

        var tally = await dbContext
            .Database.SqlQuery<int>($"select coalesce(sum(count), 0)::int as \"Value\" from stub.note_tallies where text = {query.Text}")
            .SingleAsync(cancellationToken);
        runs.Add(query.Text, tally);
        return new HeldTally(tally, context.Continue().IsTrustedContinuation);
    }
}

// Renames the folder it reads and answers the new name: a query that changes tracked state, which the Kit refuses. With
// a Hold, its handler reports that it runs, then waits to be let go before the change.
internal sealed record RenameFolderInQueryQuery(Guid FolderId, Hold? Hold = null) : Query<string>, ICacheableQuery
{
    public string CacheKey => FolderId.ToString();

    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);

    public IReadOnlyCollection<string> CacheTags => [$"folder:{FolderId}"];
}

internal sealed class RenameFolderInQueryQueryHandler(IRepository<Folder, FolderId> folders, CachedQueryRuns runs)
    : QueryHandler<RenameFolderInQueryQuery, string>
{
    public override async Task<Result<string>> Handle(RenameFolderInQueryQuery query, CancellationToken cancellationToken)
    {
        var folder = (await folders.FindAsync(new FolderId(query.FolderId), cancellationToken))!;
        runs.Add(query.FolderId.ToString(), 1);
        if (query.Hold is { } hold)
        {
            hold.Changed.TrySetResult();
            await hold.Released.Task.WaitAsync(cancellationToken);
        }

        folder.Rename("renamed in a read");
        return folder.Name;
    }
}

// Answers a folder's label colour through the Module's repository, so it reads the colour its Operation tracks, changed
// or not.
internal sealed record FolderColourQuery(Guid FolderId) : Query<string>, ICacheableQuery
{
    public string CacheKey => FolderId.ToString();

    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);

    public IReadOnlyCollection<string> CacheTags => [$"folder:{FolderId}"];
}

internal sealed class FolderColourQueryHandler(IRepository<Folder, FolderId> folders, CachedQueryRuns runs)
    : QueryHandler<FolderColourQuery, string>
{
    public override async Task<Result<string>> Handle(FolderColourQuery query, CancellationToken cancellationToken)
    {
        var folder = (await folders.FindAsync(new FolderId(query.FolderId), cancellationToken))!;
        runs.Add(query.FolderId.ToString(), 1);
        return folder.Label.Colour;
    }
}

// An uncached query that relabels the folder it reads, when told to, then reads its colour with the cacheable query
// nested.
internal sealed record RelabelThenReadColourQuery(Guid FolderId, bool Relabel) : Query<string>;

internal sealed class RelabelThenReadColourQueryHandler(IRepository<Folder, FolderId> folders, IDispatcher dispatcher)
    : QueryHandler<RelabelThenReadColourQuery, string>
{
    public override async Task<Result<string>> Handle(RelabelThenReadColourQuery query, CancellationToken cancellationToken)
    {
        if (query.Relabel)
        {
            (await folders.FindAsync(new FolderId(query.FolderId), cancellationToken))!.Relabel("relabelled in a read");
        }

        return await dispatcher.QueryAsync(new FolderColourQuery(query.FolderId), cancellationToken);
    }
}

// Its authorizer relabels the folder, then reads its colour with the cacheable query, before the Unit of Work starts.
internal sealed record RelabelInAuthorizationCommand(Guid FolderId) : Command;

internal sealed class RelabelInAuthorizationCommandAuthorizer(IRepository<Folder, FolderId> folders, IDispatcher dispatcher)
    : IAuthorizer<RelabelInAuthorizationCommand>
{
    public async Task<Result> AuthorizeAsync(RelabelInAuthorizationCommand command, CancellationToken cancellationToken)
    {
        (await folders.FindAsync(new FolderId(command.FolderId), cancellationToken))!.Relabel("relabelled in authorization");
        var colour = await dispatcher.QueryAsync(new FolderColourQuery(command.FolderId), cancellationToken);
        return colour.IsSuccess ? Result.Success() : colour.Errors;
    }
}

internal sealed class RelabelInAuthorizationCommandHandler : CommandHandler<RelabelInAuthorizationCommand>
{
    public override Task<Result> Handle(RelabelInAuthorizationCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}

/// <summary>What <see cref="FolderNamesQuery"/> answers: names enumerated only when the answer is serialized.</summary>
internal sealed record FolderNames(IEnumerable<string> Names);

// Answers a deferred enumerable that renames the folder it read when enumerated, which serializing the answer does after
// the handler has returned. With a Hold, its handler reports that it runs, then waits to be let go before it answers.
internal sealed record FolderNamesQuery(Guid FolderId, Hold? Hold = null) : Query<FolderNames>, ICacheableQuery
{
    public string CacheKey => FolderId.ToString();

    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);

    public IReadOnlyCollection<string> CacheTags => [$"folder:{FolderId}"];
}

internal sealed class FolderNamesQueryHandler(IRepository<Folder, FolderId> folders, CachedQueryRuns runs)
    : QueryHandler<FolderNamesQuery, FolderNames>
{
    public override async Task<Result<FolderNames>> Handle(FolderNamesQuery query, CancellationToken cancellationToken)
    {
        var folder = (await folders.FindAsync(new FolderId(query.FolderId), cancellationToken))!;
        runs.Add(query.FolderId.ToString(), 1);
        if (query.Hold is { } hold)
        {
            hold.Changed.TrySetResult();
            await hold.Released.Task.WaitAsync(cancellationToken);
        }

        return new FolderNames(RenamedWhenEnumerated(folder));
    }

    private static IEnumerable<string> RenamedWhenEnumerated(Folder folder)
    {
        folder.Rename("renamed while serialized");
        yield return folder.Name;
    }
}
