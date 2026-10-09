using Inventory.Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Inventory.Infrastructure.Data.Migrations;

// The category each product is filed under, from ProductCreatedV2, written with plain SQL and never mapped by EF Core,
// so the model snapshot is unchanged.
[DbContext(typeof(InventoryDbContext))]
[Migration("20261007120000_ProductCategories")]
internal sealed class ProductCategories : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            "create table inventory.product_categories (product_id uuid primary key, category_id uuid not null, filed_by text not null)"
        );

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("drop table inventory.product_categories");
}
