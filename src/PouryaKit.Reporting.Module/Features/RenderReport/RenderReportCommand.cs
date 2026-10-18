using PouryaKit.Cqrs;

namespace PouryaKit.Reporting.Module;

// Generation attempt Attempt: lays the report out, renders it, stores its file under the attempt's own key and settles
// the job, if the attempt still holds the claim.
internal sealed record RenderReportCommand(Guid JobId, int Attempt) : Command;
