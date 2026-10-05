using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PersistencePreparation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                schema: "catalog",
                table: "products",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "created_by",
                schema: "catalog",
                table: "products",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                schema: "catalog",
                table: "products",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "deleted_by",
                schema: "catalog",
                table: "products",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_deleted",
                schema: "catalog",
                table: "products",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_changed_at",
                schema: "catalog",
                table: "products",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "last_changed_by",
                schema: "catalog",
                table: "products",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ix_products_name",
                schema: "catalog",
                table: "products",
                column: "name",
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
                name: "created_at",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "created_by",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "deleted_by",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "is_deleted",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "last_changed_at",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "last_changed_by",
                schema: "catalog",
                table: "products");
        }
    }
}
