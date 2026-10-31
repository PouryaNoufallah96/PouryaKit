using Weasel.Postgresql.Tables;

namespace PouryaKit.Messaging.Wolverine.Marten;

// A Stream Module's Receipt and Snapshot version tables, of the shapes CreateReceiptTable and CreateSnapshotVersionTable
// give an EF Module's, created by Marten's own schema management with the store when the Host starts.
internal static class ReceiptTables
{
    public static IEnumerable<Table> In(string schema)
    {
        var receipts = new Table($"{schema}.{ReceiptStatements.ReceiptTable}")
        {
            PrimaryKeyName = $"pk_{ReceiptStatements.ReceiptTable}",
        };
        receipts.AddColumn("subscription", "text").AsPrimaryKey();
        receipts.AddColumn("message_id", "uuid").AsPrimaryKey();
        yield return receipts;

        var versions = new Table($"{schema}.{ReceiptStatements.SnapshotVersionTable}")
        {
            PrimaryKeyName = $"pk_{ReceiptStatements.SnapshotVersionTable}",
        };
        versions.AddColumn("subscription", "text").AsPrimaryKey();
        versions.AddColumn("aggregate_id", "text").AsPrimaryKey();
        versions.AddColumn("version", "bigint").NotNull();
        yield return versions;
    }
}
