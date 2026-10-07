using Catalog.Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PouryaKit.Persistence.EFCore;

namespace Catalog.Infrastructure.Data.Migrations;

// The keys of Catalog's idempotent commands. The table is not in the model, so it leaves the snapshot unchanged.
[DbContext(typeof(CatalogDbContext))]
[Migration("20261007120000_IdempotencyKeys")]
internal sealed class IdempotencyKeys : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateIdempotencyTable("catalog");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropIdempotencyTable("catalog");
}
