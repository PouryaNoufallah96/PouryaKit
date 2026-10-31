using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Reporting.Module;

// The Host decides who may request which report: refused here, before validation and the Unit of Work, the request
// stores no job and schedules nothing.
internal sealed class RequestReportAuthorizer(
    IReportRequestAuthorizer host,
    OperationContext context
) : IAuthorizer<RequestReportCommand>
{
    public async Task<Result> AuthorizeAsync(
        RequestReportCommand operation,
        CancellationToken cancellationToken
    ) =>
        await host.MayRequestAsync(context, operation.ToRequest(), cancellationToken)
            ? Result.Success()
            : ReportJobErrors.RequestDenied;
}
