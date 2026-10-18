using PouryaKit.AI.Postgres;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace ModularMonolith.Tests.Vectors;

/// <summary>Stores <paramref name="Text"/> under <paramref name="Key"/>, then fails when <paramref name="Fail"/> is set.</summary>
internal sealed record StorePassageCommand(string Key, string Text, bool Fail = false) : Command;

internal sealed class StorePassageCommandHandler(IVectorStore vectors) : CommandHandler<StorePassageCommand>
{
    public static readonly Error Refused = Error.Conflict("Passage.Refused", "The passage was refused after it was stored.");

    public override async Task<Result> Handle(StorePassageCommand command, CancellationToken cancellationToken)
    {
        await vectors.StoreAsync(VectorsModule.Passages, command.Key, command.Text, cancellationToken);
        return command.Fail ? Refused : Result.Success();
    }
}

/// <summary>Stores <paramref name="Text"/> under <paramref name="Key"/>, then returns the passages nearest it, read in the same Unit of Work.</summary>
internal sealed record StoreThenSearchPassageCommand(string Key, string Text) : Command<IReadOnlyList<VectorMatch>>;

internal sealed class StoreThenSearchPassageCommandHandler(IVectorStore vectors)
    : CommandHandler<StoreThenSearchPassageCommand, IReadOnlyList<VectorMatch>>
{
    public override async Task<Result<IReadOnlyList<VectorMatch>>> Handle(StoreThenSearchPassageCommand command, CancellationToken cancellationToken)
    {
        await vectors.StoreAsync(VectorsModule.Passages, command.Key, command.Text, cancellationToken);
        return Result.Success(await vectors.SearchAsync(VectorsModule.Passages, command.Text, 1, cancellationToken));
    }
}

/// <summary>Removes the passage under <paramref name="Key"/>.</summary>
internal sealed record RemovePassageCommand(string Key) : Command;

internal sealed class RemovePassageCommandHandler(IVectorStore vectors) : CommandHandler<RemovePassageCommand>
{
    public override async Task<Result> Handle(RemovePassageCommand command, CancellationToken cancellationToken)
    {
        await vectors.RemoveAsync(VectorsModule.Passages, command.Key, cancellationToken);
        return Result.Success();
    }
}

/// <summary>The <paramref name="Top"/> passages nearest <paramref name="Text"/>.</summary>
internal sealed record SearchPassagesQuery(string Text, int Top) : Query<IReadOnlyList<VectorMatch>>;

internal sealed class SearchPassagesQueryHandler(IVectorStore vectors) : QueryHandler<SearchPassagesQuery, IReadOnlyList<VectorMatch>>
{
    public override async Task<Result<IReadOnlyList<VectorMatch>>> Handle(SearchPassagesQuery query, CancellationToken cancellationToken) =>
        Result.Success(await vectors.SearchAsync(VectorsModule.Passages, query.Text, query.Top, cancellationToken));
}
