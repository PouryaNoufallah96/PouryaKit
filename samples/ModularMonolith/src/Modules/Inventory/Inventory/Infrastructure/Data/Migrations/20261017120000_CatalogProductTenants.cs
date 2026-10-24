using Inventory.Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Inventory.Infrastructure.Data.Migrations;

// Catalog's products belong to a tenant, so Inventory keys its copies of them by tenant and product, each written under
// the tenant its delivery carries. A copy made before kept no tenant, and none can be recovered: the Snapshot versions
// already applied stop a replay from writing it again. So the migration refuses a table that holds a copy rather than
// guess one. Neither table is in the model, so the model snapshot is unchanged.
[DbContext(typeof(InventoryDbContext))]
[Migration("20261017120000_CatalogProductTenants")]
internal sealed class CatalogProductTenants : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            do $$ begin
                if exists (select from inventory.catalog_products) or exists (select from inventory.product_categories) then
                    raise exception 'Inventory holds copies of Catalog''s products that kept no tenant, and their tenant cannot be recovered, so this migration refuses to guess one.';
                end if;
            end $$
            """
        );
        foreach (var table in new[] { "catalog_products", "product_categories" })
        {
            migrationBuilder.Sql(
                $"alter table inventory.{table} add column tenant text not null, drop constraint {table}_pkey, add primary key (tenant, product_id)"
            );
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[] { "catalog_products", "product_categories" })
        {
            migrationBuilder.Sql($"alter table inventory.{table} drop constraint {table}_pkey, drop column tenant, add primary key (product_id)");
        }
    }
}
