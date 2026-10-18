using PouryaKit.Cqrs;

namespace PouryaKit.Reporting.Module;

/// <summary>
/// The Host's port that decides whether an Actor may request a report, implemented by the Host that composes the report
/// Module and registered in its container. The Module asks it in the request's authorization step, before any job is
/// stored or generation scheduled: a refusal stores nothing and schedules nothing.
/// </summary>
public interface IReportRequestAuthorizer
{
    /// <summary>
    /// True when the Actor and tenant of <paramref name="context"/> may request <paramref name="request"/>: its kind,
    /// format and parameters as the caller gave them, not yet validated.
    /// </summary>
    Task<bool> MayRequestAsync(OperationContext context, ReportRequest request, CancellationToken cancellationToken);
}
