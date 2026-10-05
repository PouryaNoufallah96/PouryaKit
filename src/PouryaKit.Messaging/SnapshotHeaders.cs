namespace PouryaKit.Messaging;

/// <summary>
/// The message headers that say which aggregate a Snapshot is of and which version of it, so the receiving side applies
/// only a newer one, and identifies the Snapshot by both. Every transport writes and reads these names.
/// </summary>
public static class SnapshotHeaders
{
    /// <summary>The aggregate's key, as the publishing Module stores it.</summary>
    public const string AggregateId = "pouryakit-snapshot-aggregate-id";

    /// <summary>The aggregate's version the Snapshot was built at, the one committed with it.</summary>
    public const string Version = "pouryakit-snapshot-version";
}
