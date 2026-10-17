using Inventory.Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Inventory.Infrastructure.Data.Migrations;

// Catalog's products belong to a tenant, so Inventory keys its copies of them by tenant and product, each written under
// the tenant its delivery carries. A copy made before has no tenant to read and keeps an empty one. Neither table is in
// the model, so the model snapshot is unchanged.
[DbContext(typeof(InventoryDbContext))]
[Migration("20261017120000_CatalogProductTenants")]
internal sealed class CatalogProductTenants : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[] { "catalog_products", "product_categories" })
        {
            migrationBuilder.Sql(
                $"alter table inventory.{table} add column tenant text not null default '', drop constraint {table}_pkey, add primary key (tenant, product_id)"
            );
            migrationBuilder.Sql($"alter table inventory.{table} alter column tenant drop default");
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
