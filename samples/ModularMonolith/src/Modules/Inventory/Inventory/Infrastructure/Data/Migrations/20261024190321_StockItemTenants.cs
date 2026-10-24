using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class StockItemTenants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A stock item opened before keeps no tenant, which cannot be recovered: GetStockItem answers it to no Operation,
            // and the reconciliation, which reads every tenant's, still counts it.
            migrationBuilder.AddColumn<string>(
                name: "tenant",
                schema: "inventory",
                table: "stock_items",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "tenant",
                schema: "inventory",
                table: "stock_items");
        }
    }
}
