using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PouryaKit.AI.Postgres;

namespace ModularMonolith.Tests.Vectors.Migrations;

/// <summary>Creates the Module's vector collection in the package's schema.</summary>
[DbContext(typeof(VectorsDbContext))]
[Migration("20261018120000_Passages")]
public sealed class Passages : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateVectorCollection(VectorsModule.Passages, VectorsModule.Dimensions);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropVectorCollection(VectorsModule.Passages);
}
