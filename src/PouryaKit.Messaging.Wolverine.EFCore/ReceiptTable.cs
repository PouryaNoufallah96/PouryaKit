using Microsoft.EntityFrameworkCore.Migrations;

namespace PouryaKit.Messaging.Wolverine.EFCore;

/// <summary>
/// The table a Module keeps its handlers' Receipts in, in its own schema beside its data, so a Receipt commits on the
/// Module's transaction. It is not part of the Module's model: a migration creates it with
/// <see cref="CreateReceiptTable"/>, once, in every Module that declares a Notification Handler or subscribes an
/// Integration Event Handler. A Module that publishes Integration Events also keeps here one row per Domain Event and
/// wire name published for it, keyed <c>&lt;wire name&gt;#published</c>.
/// </summary>
public static class ReceiptTable
{
    public const string Name = "pouryakit_receipts";

    extension(MigrationBuilder migrationBuilder)
    {
        /// <summary>Creates the Receipt table in <paramref name="schema"/>, the Module's own.</summary>
        public void CreateReceiptTable(string schema) =>
            migrationBuilder.CreateTable(
                name: Name,
                schema: schema,
                columns: table => new
                {
                    subscription = table.Column<string>(type: "text", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                },
                constraints: table => table.PrimaryKey($"pk_{Name}", receipt => new { receipt.subscription, receipt.message_id })
            );

        public void DropReceiptTable(string schema) => migrationBuilder.DropTable(name: Name, schema: schema);
    }
}
