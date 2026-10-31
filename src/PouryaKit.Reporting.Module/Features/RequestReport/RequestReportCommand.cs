using PouryaKit.Cqrs;

namespace PouryaKit.Reporting.Module;

// Stores a report job and schedules its first claim; returns the job's id.
internal sealed record RequestReportCommand(
    string Kind,
    ReportFormat Format,
    IReadOnlyDictionary<string, string> Parameters
) : Command<Guid>
{
    public ReportRequest ToRequest() => new(Kind, Format, Parameters);
}
