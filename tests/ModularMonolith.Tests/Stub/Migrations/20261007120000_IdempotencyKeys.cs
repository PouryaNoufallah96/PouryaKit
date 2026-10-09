using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PouryaKit.Persistence.EFCore;

namespace ModularMonolith.Tests.Stub.Migrations;

// The keys of the Stub's idempotent commands. The table is not in the model, so it leaves the snapshot unchanged.
[DbContext(typeof(StubDbContext))]
[Migration("20261007120000_IdempotencyKeys")]
internal sealed class IdempotencyKeys : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateIdempotencyTable("stub");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropIdempotencyTable("stub");
}
