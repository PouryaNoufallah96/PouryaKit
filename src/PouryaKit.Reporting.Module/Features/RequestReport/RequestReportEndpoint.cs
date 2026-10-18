using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using PouryaKit.Web;

namespace PouryaKit.Reporting.Module;

// 202 with the job's id and its location: the report is generated in the background, and the job endpoint answers its
// state and, once it is ready, its download link.
internal sealed class RequestReportEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapPost(
            "",
            async (
                RequestReportBody body,
                OperationContext context,
                IGatewayProcessor<ReportModule> reports,
                HttpContext httpContext,
                LinkGenerator links,
                CancellationToken cancellationToken
            ) =>
                (
                    await reports.SendAsync(
                        new RequestReportCommand(body.Kind ?? "", body.Format, body.Parameters ?? []),
                        context,
                        cancellationToken
                    )
                ).ToHttpResult(id =>
                    TypedResults.Accepted(links.GetPathByName(httpContext, GetReportJobEndpoint.Name, new { jobId = id }), new { id })
                )
        );
}

// A format is required, so a body without one fails to bind and answers 400, as one naming no format does; it would
// otherwise read as the enum's first member, Pdf.
internal sealed record RequestReportBody(
    string? Kind,
    [property: JsonRequired, JsonConverter(typeof(JsonStringEnumConverter<ReportFormat>))] ReportFormat Format,
    Dictionary<string, string>? Parameters
);
