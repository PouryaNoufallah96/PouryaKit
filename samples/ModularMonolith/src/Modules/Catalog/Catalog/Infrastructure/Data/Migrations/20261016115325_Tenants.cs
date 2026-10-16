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
            migrationBuilder.DropIndex(
                name: "ix_products_name",
                schema: "catalog",
                table: "products");

            migrationBuilder.AddColumn<string>(
                name: "tenant",
                schema: "catalog",
                table: "products",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "tenant",
                schema: "catalog",
                table: "categories",
                type: "text",
                nullable: false,
                defaultValue: "");

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
