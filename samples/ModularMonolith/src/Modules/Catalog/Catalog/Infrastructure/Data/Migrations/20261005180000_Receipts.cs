using Catalog.Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PouryaKit.Messaging.Wolverine.EFCore;

namespace Catalog.Infrastructure.Data.Migrations;

#region receipts-migration
// The Receipts of Catalog's Notification Handlers. The table is not in the model, so it leaves the snapshot unchanged.
[DbContext(typeof(CatalogDbContext))]
[Migration("20261005180000_Receipts")]
internal sealed class Receipts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateReceiptTable("catalog");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropReceiptTable("catalog");
}
#endregion
