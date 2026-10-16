using Weasel.Postgresql;
using Weasel.Postgresql.Tables;

namespace M2.Marten;

// The Receipt, Snapshot version and idempotency tables of a Stream Module, in its own schema, with the shapes of the EF
// Module's (ReceiptTable, SnapshotVersionTable, IdempotencyTable), created by Marten's own schema management as
// extended schema objects of the store.
internal static class MartenTables
{
    public const string Receipts = "pouryakit_receipts";
    public const string SnapshotVersions = "pouryakit_snapshot_versions";
    public const string IdempotencyKeys = "pouryakit_idempotency_keys";

    public static IEnumerable<Table> Of(string schema)
    {
        var receipts = new Table(new PostgresqlObjectName(schema, Receipts));
        receipts.AddColumn("subscription", "text").AsPrimaryKey();
        receipts.AddColumn("message_id", "uuid").AsPrimaryKey();
        yield return receipts;

        var versions = new Table(new PostgresqlObjectName(schema, SnapshotVersions));
        versions.AddColumn("subscription", "text").AsPrimaryKey();
        versions.AddColumn("aggregate_id", "text").AsPrimaryKey();
        versions.AddColumn("version", "bigint").NotNull();
        yield return versions;

        var keys = new Table(new PostgresqlObjectName(schema, IdempotencyKeys));
        keys.AddColumn("tenant", "text").AsPrimaryKey();
        keys.AddColumn("actor_kind", "text").AsPrimaryKey();
        keys.AddColumn("actor_id", "text").AsPrimaryKey();
        keys.AddColumn("command", "text").AsPrimaryKey();
        keys.AddColumn("request_id", "text").AsPrimaryKey();
        keys.AddColumn("response", "text");
        keys.AddColumn("reserved_at", "timestamp with time zone").NotNull();
        keys.Indexes.Add(new IndexDefinition($"ix_{IdempotencyKeys}_reserved_at") { Columns = ["reserved_at"] });
        yield return keys;
    }
}
