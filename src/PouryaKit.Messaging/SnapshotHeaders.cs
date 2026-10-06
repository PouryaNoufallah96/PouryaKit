namespace PouryaKit.Messaging;

/// <summary>
/// The message headers that say which aggregate a Snapshot is of and which version of it, so the receiving side applies
/// only a newer one, and identifies the Snapshot by both. Every transport writes and reads these names.
/// </summary>
public static class SnapshotHeaders
{
    /// <summary>
    /// The aggregate's provider key, encoded without losing precision: Guid, int, long and string keep their formats,
    /// byte arrays use uppercase hexadecimal, and losslessly mapped PostgreSQL timestamps use DateTime ticks without
    /// Kind. Composite keys prefix each part with its invariant length and a colon. Null and other types are refused.
    /// </summary>
    public const string AggregateId = "pouryakit-snapshot-aggregate-id";

    /// <summary>The aggregate's version the Snapshot was built at, the one committed with it.</summary>
    public const string Version = "pouryakit-snapshot-version";
}
