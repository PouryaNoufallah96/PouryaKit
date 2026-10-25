using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProductConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "product_conversations",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    session = table.Column<string>(type: "json", nullable: true),
                    memory = table.Column<string>(type: "json", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_conversations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_product_conversations_key",
                schema: "catalog",
                table: "product_conversations",
                column: "key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "product_conversations",
                schema: "catalog");
        }
    }
}
