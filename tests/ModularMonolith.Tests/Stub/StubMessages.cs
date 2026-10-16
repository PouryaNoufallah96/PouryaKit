using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Localization;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Modularity;
using PouryaKit.Web;

namespace ModularMonolith.Tests.Stub;

// The Stub Module's localized messages: StubMessages.resx in English, StubMessages.fa.resx in Persian, beside this file.
internal sealed class StubMessages;

internal sealed record GreetQuery : Query<string>;

internal sealed class GreetQueryHandler(IStringLocalizer<StubMessages> messages) : QueryHandler<GreetQuery, string>
{
    public override Task<Result<string>> Handle(GreetQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<Result<string>>(messages["Greeting"].Value);
}

internal sealed class GreetingEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapGet(
            "/greeting",
            async (OperationContext context, IGatewayProcessor<StubModule> stub, CancellationToken cancellationToken) =>
                (await stub.QueryAsync(new GreetQuery(), context, cancellationToken)).ToHttpResult()
        );
}
