using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ModularMonolith.Tests.Stub.Migrations
{
    /// <inheritdoc />
    public partial class TenantBinders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "binders",
                schema: "stub",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_binders", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "dockets",
                schema: "stub",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    tenant = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dockets", x => new { x.tenant, x.id });
                });

            migrationBuilder.CreateTable(
                name: "sheets",
                schema: "stub",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    binder_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sheets", x => x.id);
                    table.ForeignKey(
                        name: "fk_sheets_binders_binder_id",
                        column: x => x.binder_id,
                        principalSchema: "stub",
                        principalTable: "binders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sheets_binder_id",
                schema: "stub",
                table: "sheets",
                column: "binder_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dockets",
                schema: "stub");

            migrationBuilder.DropTable(
                name: "sheets",
                schema: "stub");

            migrationBuilder.DropTable(
                name: "binders",
                schema: "stub");
        }
    }
}
