using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ModularMonolith.Tests.Stub.Migrations
{
    /// <inheritdoc />
    public partial class SnapshotKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "snapshot_key_copies",
                schema: "stub",
                columns: table => new
                {
                    kind = table.Column<string>(type: "text", nullable: false),
                    id = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_snapshot_key_copies", x => new { x.kind, x.id });
                });

            migrationBuilder.CreateTable(
                name: "binary_keys",
                schema: "stub",
                columns: table => new
                {
                    id = table.Column<byte[]>(type: "bytea", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_binary_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "timestamp_keys",
                schema: "stub",
                columns: table => new
                {
                    id = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_timestamp_keys", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "snapshot_key_copies",
                schema: "stub");

            migrationBuilder.DropTable(
                name: "binary_keys",
                schema: "stub");

            migrationBuilder.DropTable(
                name: "timestamp_keys",
                schema: "stub");
        }
    }
}
