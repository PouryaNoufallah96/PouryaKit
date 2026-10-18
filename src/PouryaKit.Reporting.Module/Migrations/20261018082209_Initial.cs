using System;
using Microsoft.EntityFrameworkCore.Migrations;
using PouryaKit.Messaging.Wolverine.EFCore;

#nullable disable

namespace PouryaKit.Reporting.Module
{
    internal sealed partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "reports");

            migrationBuilder.CreateTable(
                name: "report_jobs",
                schema: "reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    format = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    parameters = table.Column<string>(type: "jsonb", nullable: false),
                    requested_by = table.Column<string>(type: "text", nullable: false),
                    requested_by_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    tenant = table.Column<string>(type: "text", nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    file_key = table.Column<string>(type: "text", nullable: true),
                    failure = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_report_jobs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_report_jobs_state_expires_at",
                schema: "reports",
                table: "report_jobs",
                columns: ["state", "expires_at"]);

            // The Receipts of the Module's delayed and recurring commands; not in the model, so not in the snapshot.
            migrationBuilder.CreateReceiptTable("reports");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropReceiptTable("reports");

            migrationBuilder.DropTable(
                name: "report_jobs",
                schema: "reports");
        }
    }
}
