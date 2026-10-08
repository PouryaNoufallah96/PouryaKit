using PouryaKit.Caching;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

// The Stub Module's namespace, on purpose: each type here has the namespace-qualified name of one in the Stub Module's
// assembly (Stub/StubSameNamed.cs there), and its handler answers this assembly's name instead.
namespace ModularMonolith.Tests.Stub;

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
