using PouryaKit.Domain;

namespace PouryaKit.Reporting.Module;

internal static class ReportJobErrors
{
    public static readonly Error RequestDenied = Error.Forbidden(
        "Reports.RequestDenied",
        "This report may not be requested with these parameters."
    );

    // Another Actor's job, another tenant's or one that never existed: the answer is the same, so a job id grants nothing.
    public static readonly Error NotFound = Error.NotFound(
        "Reports.JobNotFound",
        "The report job does not exist."
    );
}
