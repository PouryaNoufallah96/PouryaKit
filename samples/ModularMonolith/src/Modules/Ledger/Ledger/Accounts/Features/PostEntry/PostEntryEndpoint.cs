using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using PouryaKit.Web;

namespace Ledger.Accounts.Features.PostEntry;

internal sealed class PostEntryEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapPost(
            "/accounts/{accountId}/entries",
            async (
                Guid accountId,
                PostEntryBody body,
                OperationContext context,
                IGatewayProcessor<LedgerModule> ledger,
                CancellationToken cancellationToken
            ) =>
                (
                    await ledger.SendAsync(
                        new PostEntryCommand(accountId, body.Amount),
                        context,
                        cancellationToken
                    )
                ).ToHttpResult()
        );
}

internal sealed record PostEntryBody(decimal Amount);
