using Inventory.Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Inventory.Infrastructure.Data.Migrations;

// One row per occurrence of the recurring stock reconciliation, written with plain SQL and never mapped by EF Core, so
// the model snapshot is unchanged. Nothing makes an occurrence unique here: the Kit's Receipt does.
[DbContext(typeof(InventoryDbContext))]
[Migration("20261007150000_StockReconciliations")]
internal sealed class StockReconciliations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            "create table inventory.stock_reconciliations (id bigint generated always as identity primary key, occurrence timestamptz not null, stock_items bigint not null, reconciled_by text not null)"
        );

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("drop table inventory.stock_reconciliations");
}
