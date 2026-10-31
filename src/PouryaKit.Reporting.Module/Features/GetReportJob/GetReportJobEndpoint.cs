using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using PouryaKit.Web;

namespace PouryaKit.Reporting.Module;

// No :guid route constraint: an id that does not parse is the caller's mistake (400), not a missing route (404).
internal sealed class GetReportJobEndpoint : IEndpoint
{
    public const string Name = "GetReportJob";

    public void Map(IEndpointRouteBuilder routes) =>
        routes
            .MapGet(
                "/{jobId}",
                async (
                    Guid jobId,
                    OperationContext context,
                    IGatewayProcessor<ReportModule> reports,
                    CancellationToken cancellationToken
                ) =>
                    (
                        await reports.QueryAsync(
                            new GetReportJobQuery(jobId),
                            context,
                            cancellationToken
                        )
                    ).ToHttpResult()
            )
            .WithName(Name);
}
