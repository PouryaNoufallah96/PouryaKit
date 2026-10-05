using PouryaKit.Domain;

namespace Offending.Contracts;

// A published message that carries a Kit type, so the Contracts project references the Kit.
public sealed record OffendingCreatedV1(Guid Id, ErrorKind Kind);
