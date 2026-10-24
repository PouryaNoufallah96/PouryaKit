using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Reporting.Module;

/// <summary>
/// The Host's port that lays out each report kind it defines, implemented by the Host that composes the report Module
/// and registered in its container. The Module asks it in the background, for each generation attempt and again when an
/// attempt is retried, and renders the layout it returns.
/// </summary>
public interface IReportLayouts
{
    /// <summary>
    /// The layout of <paramref name="request"/>, read under <paramref name="context"/>: a continuation of the Operation
    /// that requested the report, with its Actor and tenant, so asking another Module continues it with
    /// <c>Continue()</c>. A failed Result fails the job with its first Error.
    /// </summary>
    Task<Result<ReportLayout>> LayOutAsync(OperationContext context, ReportRequest request, CancellationToken cancellationToken);
}
