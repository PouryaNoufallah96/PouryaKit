namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// The SQL every store package's <see cref="IReceipts"/> runs on its Module's tables, in the receiving Unit of Work's own
/// transaction: PostgreSQL statements whose values are composite-format items (<c>{0}</c>, <c>{1}</c>, …) the package
/// binds as parameters its own way, over a table name its registration gives, never input. Each affects one row when it
/// records and none when it turns the message away. Feature code never runs them.
/// </summary>
public static class ReceiptStatements
{
    /// <summary>The Receipt table's name, in the Module's schema.</summary>
    public const string ReceiptTable = "pouryakit_receipts";

    /// <summary>The Snapshot version table's name, in the Module's schema.</summary>
    public const string SnapshotVersionTable = "pouryakit_snapshot_versions";

    /// <summary>
    /// Records the Receipt of the subscription <c>{0}</c> for the message <c>{1}</c>. The primary key makes a second
    /// Receipt for the same message wait for the first transaction, then insert nothing.
    /// </summary>
    public static string Record(string table) => $"insert into {table} (subscription, message_id) values ({{0}}, {{1}}) on conflict do nothing";

    /// <summary>
    /// Records that the subscription <c>{0}</c> applied the version <c>{2}</c> of the aggregate <c>{1}</c>, when it is
    /// newer than the one recorded. Under read committed, a conflicting row another transaction holds makes this one wait,
    /// then decide on the row that transaction committed.
    /// </summary>
    public static string RecordVersion(string table) =>
        $"insert into {table} as applied (subscription, aggregate_id, version) values ({{0}}, {{1}}, {{2}}) on conflict (subscription, aggregate_id) do update set version = excluded.version where applied.version < excluded.version";
}
