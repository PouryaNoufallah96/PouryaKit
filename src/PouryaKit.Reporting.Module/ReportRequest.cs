namespace PouryaKit.Reporting.Module;

/// <summary>
/// What an Actor asks the report Module for: a report kind the Host defines, the format to render it to, and the
/// parameters the kind reads, such as a month, as text the Host's kind parses.
/// </summary>
/// <param name="Kind">The report kind, such as <c>sales</c>, which the Host's <see cref="IReportLayouts"/> lays out.</param>
/// <param name="Format">The file format the report is rendered to.</param>
/// <param name="Parameters">The kind's parameters by name, empty when it takes none.</param>
public sealed record ReportRequest(string Kind, ReportFormat Format, IReadOnlyDictionary<string, string> Parameters);
