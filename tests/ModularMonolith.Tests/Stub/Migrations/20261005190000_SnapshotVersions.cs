using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PouryaKit.Messaging.Wolverine.EFCore;

namespace ModularMonolith.Tests.Stub.Migrations;

// The versions of the Snapshots the Stub applied. The table is not in the model, so it leaves the snapshot unchanged.
[DbContext(typeof(StubDbContext))]
[Migration("20261005190000_SnapshotVersions")]
internal sealed class SnapshotVersions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateSnapshotVersionTable("stub");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropSnapshotVersionTable("stub");
}
