using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ModularMonolith.Tests.Stub.Migrations
{
    /// <inheritdoc />
    public partial class SnapshotGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "counters",
                schema: "stub",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_counters", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "leaflets",
                schema: "stub",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_leaflets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "shelves",
                schema: "stub",
                columns: table => new
                {
                    aisle = table.Column<string>(type: "text", nullable: false),
                    bay = table.Column<string>(type: "text", nullable: false),
                    labels = table.Column<List<string>>(type: "text[]", nullable: false),
                    size_depth = table.Column<int>(type: "integer", nullable: false),
                    size_width = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shelves", x => new { x.aisle, x.bay });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "counters",
                schema: "stub");

            migrationBuilder.DropTable(
                name: "leaflets",
                schema: "stub");

            migrationBuilder.DropTable(
                name: "shelves",
                schema: "stub");
        }
    }
}
