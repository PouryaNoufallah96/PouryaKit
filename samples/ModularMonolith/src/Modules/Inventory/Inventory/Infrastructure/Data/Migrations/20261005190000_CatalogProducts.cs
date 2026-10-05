using Inventory.Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PouryaKit.Messaging.Wolverine.EFCore;

namespace Inventory.Infrastructure.Data.Migrations;

// Inventory's copy of Catalog's products, written with plain SQL and never mapped by EF Core, and the versions of the
// Snapshots Inventory applied; neither is in the model, so the model snapshot is unchanged.
[DbContext(typeof(InventoryDbContext))]
[Migration("20261005190000_CatalogProducts")]
internal sealed class CatalogProducts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "create table inventory.catalog_products (product_id uuid primary key, name text not null, version bigint not null)"
        );
        migrationBuilder.CreateSnapshotVersionTable("inventory");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropSnapshotVersionTable("inventory");
        migrationBuilder.Sql("drop table inventory.catalog_products");
    }
}
