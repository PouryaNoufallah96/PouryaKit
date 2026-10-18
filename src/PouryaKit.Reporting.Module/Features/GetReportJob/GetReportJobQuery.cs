using System.Text.Json.Serialization;
using PouryaKit.Cqrs;

namespace PouryaKit.Reporting.Module;

internal sealed record GetReportJobQuery(Guid JobId) : Query<ReportJobDto>;

// What the requester sees of a job: its state, and the link to its file once it is completed.
internal sealed record ReportJobDto(
    Guid Id,
    string Kind,
    [property: JsonConverter(typeof(JsonStringEnumConverter<ReportFormat>))] ReportFormat Format,
    [property: JsonConverter(typeof(JsonStringEnumConverter<ReportJobState>))] ReportJobState State,
    Uri? DownloadUrl,
    string? Failure,
    DateTimeOffset ExpiresAt
);
