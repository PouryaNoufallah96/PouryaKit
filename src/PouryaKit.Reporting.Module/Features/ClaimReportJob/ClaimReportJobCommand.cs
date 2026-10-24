using PouryaKit.Cqrs;

namespace PouryaKit.Reporting.Module;

// Claims the job for generation attempt Attempt: the first when the job is requested, the second when the first
// attempt's claim has gone stale.
internal sealed record ClaimReportJobCommand(Guid JobId, int Attempt) : Command;
