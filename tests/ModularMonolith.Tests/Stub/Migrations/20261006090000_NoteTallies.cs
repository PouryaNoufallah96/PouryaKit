using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ModularMonolith.Tests.Stub.Migrations;

// A projection table written with Dapper and never mapped by EF Core, so it is created with SQL and leaves the model
// snapshot unchanged.
[DbContext(typeof(StubDbContext))]
[Migration("20261006090000_NoteTallies")]
internal sealed class NoteTallies : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("create table stub.note_tallies (text text primary key, count integer not null)");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("drop table stub.note_tallies");
}
