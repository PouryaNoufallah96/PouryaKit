using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ModularMonolith.Tests.Stub.Migrations
{
    /// <inheritdoc />
    public partial class GeneratedValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "drawers",
                schema: "stub",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    knob_finish = table.Column<string>(type: "text", nullable: false),
                    knob_label = table.Column<string>(type: "text", nullable: true, computedColumnSql: "upper(knob_finish)", stored: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_drawers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tickets",
                schema: "stub",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    serial = table.Column<string>(type: "text", nullable: true, defaultValueSql: "'printed by the database'"),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tickets", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "drawers",
                schema: "stub");

            migrationBuilder.DropTable(
                name: "tickets",
                schema: "stub");
        }
    }
}
