using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ModularMonolith.Tests.Stub.Migrations
{
    /// <inheritdoc />
    public partial class TraysAndCases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cases",
                schema: "stub",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    discriminator = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cases", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "trays",
                schema: "stub",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trays", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "books",
                schema: "stub",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    book_case_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_books", x => x.id);
                    table.ForeignKey(
                        name: "fk_books_case_book_case_id",
                        column: x => x.book_case_id,
                        principalSchema: "stub",
                        principalTable: "cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pencils",
                schema: "stub",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    colour = table.Column<string>(type: "text", nullable: false),
                    pencil_case_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pencils", x => x.id);
                    table.ForeignKey(
                        name: "fk_pencils_cases_pencil_case_id",
                        column: x => x.pencil_case_id,
                        principalSchema: "stub",
                        principalTable: "cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "cards",
                schema: "stub",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    last_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_changed_by = table.Column<string>(type: "text", nullable: false),
                    tray_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cards", x => x.id);
                    table.ForeignKey(
                        name: "fk_cards_tray_tray_id",
                        column: x => x.tray_id,
                        principalSchema: "stub",
                        principalTable: "trays",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "pins",
                schema: "stub",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    last_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_changed_by = table.Column<string>(type: "text", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<string>(type: "text", nullable: true),
                    tray_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pins", x => x.id);
                    table.ForeignKey(
                        name: "fk_pins_tray_tray_id",
                        column: x => x.tray_id,
                        principalSchema: "stub",
                        principalTable: "trays",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_books_book_case_id",
                schema: "stub",
                table: "books",
                column: "book_case_id");

            migrationBuilder.CreateIndex(
                name: "ix_cards_tray_id",
                schema: "stub",
                table: "cards",
                column: "tray_id");

            migrationBuilder.CreateIndex(
                name: "ix_pencils_pencil_case_id",
                schema: "stub",
                table: "pencils",
                column: "pencil_case_id");

            migrationBuilder.CreateIndex(
                name: "ix_pins_tray_id",
                schema: "stub",
                table: "pins",
                column: "tray_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "books",
                schema: "stub");

            migrationBuilder.DropTable(
                name: "cards",
                schema: "stub");

            migrationBuilder.DropTable(
                name: "pencils",
                schema: "stub");

            migrationBuilder.DropTable(
                name: "pins",
                schema: "stub");

            migrationBuilder.DropTable(
                name: "cases",
                schema: "stub");

            migrationBuilder.DropTable(
                name: "trays",
                schema: "stub");
        }
    }
}
