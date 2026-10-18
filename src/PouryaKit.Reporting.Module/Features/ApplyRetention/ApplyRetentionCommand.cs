using PouryaKit.Cqrs;

namespace PouryaKit.Reporting.Module;

// One occurrence of the Module's retention, which runs as its system Actor, across every tenant.
internal sealed record ApplyRetentionCommand(DateTimeOffset Occurrence) : Command;
