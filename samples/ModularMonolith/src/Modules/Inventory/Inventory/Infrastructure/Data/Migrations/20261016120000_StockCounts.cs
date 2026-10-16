using Inventory.Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PouryaKit.Persistence.Postgres;

namespace Inventory.Infrastructure.Data.Migrations;

// The numbered stock counts, written with plain SQL and never mapped by EF Core, and the Kit's number sequences their
// numbers come from; neither is in the model, so the model snapshot is unchanged.
[DbContext(typeof(InventoryDbContext))]
[Migration("20261016120000_StockCounts")]
internal sealed class StockCounts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "create table inventory.stock_counts (year integer not null, number bigint not null, started_by text not null, primary key (year, number))"
        );
        migrationBuilder.CreateNumberSequenceTable("inventory");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropNumberSequenceTable("inventory");
        migrationBuilder.Sql("drop table inventory.stock_counts");
    }
}
