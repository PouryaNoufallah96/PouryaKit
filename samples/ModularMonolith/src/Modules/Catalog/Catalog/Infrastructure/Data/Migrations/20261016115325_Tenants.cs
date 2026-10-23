using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Tenants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A row written before Catalog was tenant-scoped has no tenant to recover, and any tenant given to it, such as an
            // empty one, would be no Operation's, so it would stay hidden and could never be re-tenanted: the columns take no default.
            migrationBuilder.Sql(
                """
                do $$ begin
                    if exists (select from catalog.products) or exists (select from catalog.categories) then
                        raise exception 'Catalog holds products or categories that kept no tenant, and their tenant cannot be recovered, so this migration refuses to guess one.';
                    end if;
                end $$
                """
            );

            migrationBuilder.DropIndex(
                name: "ix_products_name",
                schema: "catalog",
                table: "products");

            migrationBuilder.AddColumn<string>(
                name: "tenant",
                schema: "catalog",
                table: "products",
                type: "text",
                nullable: false);

            migrationBuilder.AddColumn<string>(
                name: "tenant",
                schema: "catalog",
                table: "categories",
                type: "text",
                nullable: false);

            migrationBuilder.CreateIndex(
                name: "ix_products_name",
                schema: "catalog",
                table: "products",
                columns: ["tenant", "name"],
                unique: true,
                filter: "is_deleted = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_products_name",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "tenant",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "tenant",
                schema: "catalog",
                table: "categories");

            migrationBuilder.CreateIndex(
                name: "ix_products_name",
                schema: "catalog",
                table: "products",
                column: "name",
                unique: true,
                filter: "is_deleted = false");
        }
    }
}
