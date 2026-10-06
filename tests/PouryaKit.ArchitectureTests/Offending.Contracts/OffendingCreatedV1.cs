namespace Offending.Contracts;

// A plain record, as Contracts should be; the project still references the Kit, though no code uses it, so the
// compiled assembly shows no trace of the reference.
public sealed record OffendingCreatedV1(Guid Id);
