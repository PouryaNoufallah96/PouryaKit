using PouryaKit.Caching;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace ModularMonolith.Tests.Stub;

// Each answers the name of the assembly its handler lives in. The SameNamed assembly holds a query and a command with
// these namespace-qualified names, whose handlers answer that assembly's name, so an answer shows whose handler ran.
internal sealed record HandlingAssemblyQuery(string Key) : Query<string>, ICacheableQuery
{
    public string CacheKey => Key;

    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);

    public IReadOnlyCollection<string> CacheTags => [];
}

internal sealed class HandlingAssemblyQueryHandler : QueryHandler<HandlingAssemblyQuery, string>
{
    public override Task<Result<string>> Handle(HandlingAssemblyQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<Result<string>>(typeof(HandlingAssemblyQueryHandler).Assembly.GetName().Name!);
}

[Idempotent]
internal sealed record NameHandlingAssemblyCommand : Command<string>;

internal sealed class NameHandlingAssemblyCommandHandler : CommandHandler<NameHandlingAssemblyCommand, string>
{
    public override Task<Result<string>> Handle(NameHandlingAssemblyCommand command, CancellationToken cancellationToken) =>
        Task.FromResult<Result<string>>(typeof(NameHandlingAssemblyCommandHandler).Assembly.GetName().Name!);
}
