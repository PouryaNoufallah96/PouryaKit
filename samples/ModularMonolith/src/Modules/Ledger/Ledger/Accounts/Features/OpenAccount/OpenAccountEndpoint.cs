using Ledger.Accounts.Features.GetAccount;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using PouryaKit.Web;

namespace Ledger.Accounts.Features.OpenAccount;

internal sealed class OpenAccountEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapPost(
            "/accounts",
            async (
                OpenAccountBody body,
                OperationContext context,
                IGatewayProcessor<LedgerModule> ledger,
                HttpContext httpContext,
                LinkGenerator links,
                CancellationToken cancellationToken
            ) =>
                (await ledger.SendAsync(new OpenAccountCommand(body.AccountId, body.Holder), context, cancellationToken)).ToHttpResult(id =>
                    TypedResults.Created(links.GetPathByName(httpContext, GetAccountEndpoint.Name, new { accountId = id }), new { id })
                )
        );
}

internal sealed record OpenAccountBody(Guid AccountId, string Holder);
