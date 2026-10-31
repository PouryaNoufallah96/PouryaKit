using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Localization;
using PouryaKit.Caching;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Modularity;
using PouryaKit.Web;

namespace ModularMonolith.Tests.Stub;

#region greet-localized-query
// The Stub Module's localized messages: StubMessages.resx in English, StubMessages.fa.resx in Persian, beside this file.
internal sealed class StubMessages;

internal sealed record GreetQuery : Query<string>;

internal sealed class GreetQueryHandler(IStringLocalizer<StubMessages> messages)
    : QueryHandler<GreetQuery, string>
{
    public override Task<Result<string>> Handle(
        GreetQuery query,
        CancellationToken cancellationToken
    ) => Task.FromResult<Result<string>>(messages["Greeting"].Value);
}
#endregion

// The same greeting, cached: one key for every caller's culture, so only the Kit's key tells the cultures apart.
internal sealed record GreetCachedQuery : Query<string>, ICacheableQuery
{
    public string CacheKey => "greeting";

    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);

    public IReadOnlyCollection<string> CacheTags => ["greeting"];
}

internal sealed class GreetCachedQueryHandler(IStringLocalizer<StubMessages> messages)
    : QueryHandler<GreetCachedQuery, string>
{
    public override Task<Result<string>> Handle(
        GreetCachedQuery query,
        CancellationToken cancellationToken
    ) => Task.FromResult<Result<string>>(messages["Greeting"].Value);
}

internal sealed class GreetingEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet(
            "/greeting",
            async (
                OperationContext context,
                IGatewayProcessor<StubModule> stub,
                CancellationToken cancellationToken
            ) =>
                (await stub.QueryAsync(new GreetQuery(), context, cancellationToken)).ToHttpResult()
        );
        routes.MapGet(
            "/greeting/cached",
            async (
                OperationContext context,
                IGatewayProcessor<StubModule> stub,
                CancellationToken cancellationToken
            ) =>
                (
                    await stub.QueryAsync(new GreetCachedQuery(), context, cancellationToken)
                ).ToHttpResult()
        );
    }
}
