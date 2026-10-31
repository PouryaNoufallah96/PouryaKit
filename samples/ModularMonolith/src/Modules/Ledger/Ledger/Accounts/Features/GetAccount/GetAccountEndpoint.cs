using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using PouryaKit.Web;

namespace Ledger.Accounts.Features.GetAccount;

internal sealed class GetAccountEndpoint : IEndpoint
{
    /// <summary>The endpoint's name, which a link to an account names.</summary>
    public const string Name = "GetAccount";

    public void Map(IEndpointRouteBuilder routes) =>
        routes
            .MapGet(
                "/accounts/{accountId}",
                async (
                    Guid accountId,
                    OperationContext context,
                    IGatewayProcessor<LedgerModule> ledger,
                    CancellationToken cancellationToken
                ) =>
                    (
                        await ledger.QueryAsync(
                            new GetAccountQuery(accountId),
                            context,
                            cancellationToken
                        )
                    ).ToHttpResult()
            )
            .WithName(Name);
}
