using Microsoft.EntityFrameworkCore.Migrations;

namespace PouryaKit.Messaging.Wolverine.EFCore;

/// <summary>
/// The table a Module keeps, beside its Receipts, the newest version of each aggregate each of its subscriptions to a
/// Snapshot has applied, so a Snapshot is applied only when it is newer and a late one never undoes a newer one or a
/// Tombstone. It is not part of the Module's model: a migration creates it with
/// <see cref="CreateSnapshotVersionTable"/>, once, in every Module that subscribes to a Snapshot. A row is never removed
/// and its version only grows, so the key of an aggregate deleted outright must never be used again: the new
/// aggregate's Snapshots would be turned away until its version passed the old Tombstone's.
/// </summary>
public static class SnapshotVersionTable
{
    /// <summary>The Snapshot version table's name, in the Module's schema.</summary>
    public const string Name = ReceiptStatements.SnapshotVersionTable;

    extension(MigrationBuilder migrationBuilder)
    {
        /// <summary>Creates the Snapshot version table in <paramref name="schema"/>, the Module's own.</summary>
        public void CreateSnapshotVersionTable(string schema) =>
            migrationBuilder.CreateTable(
                name: Name,
                schema: schema,
                columns: table => new
                {
                    subscription = table.Column<string>(type: "text", nullable: false),
                    aggregate_id = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                    table.PrimaryKey(
                        $"pk_{Name}",
                        applied => new { applied.subscription, applied.aggregate_id }
                    )
            );

        /// <summary>Drops the Snapshot version table from <paramref name="schema"/>, the Module's own.</summary>
        public void DropSnapshotVersionTable(string schema) =>
            migrationBuilder.DropTable(name: Name, schema: schema);
    }
}
