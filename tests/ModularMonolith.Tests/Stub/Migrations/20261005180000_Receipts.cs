using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PouryaKit.Messaging.Wolverine.EFCore;

namespace ModularMonolith.Tests.Stub.Migrations;

// The Receipts of the Stub's Notification Handlers. The table is not in the model, so it leaves the snapshot unchanged.
[DbContext(typeof(StubDbContext))]
[Migration("20261005180000_Receipts")]
internal sealed class Receipts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateReceiptTable("stub");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropReceiptTable("stub");
}
