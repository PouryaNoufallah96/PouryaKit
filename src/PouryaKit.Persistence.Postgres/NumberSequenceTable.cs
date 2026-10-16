using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace PouryaKit.Persistence.Postgres;

/// <summary>
/// The table a Module keeps its number sequences in, in its own schema beside its data, so a number is taken on the
/// Module's transaction and released by its rollback. One row per scope key holds the last number given out. It is not
/// part of the Module's model: a migration creates it with <see cref="CreateNumberSequenceTable"/>, once, in every
/// Module that asks <see cref="INumberSequences"/> for a number.
/// </summary>
public static class NumberSequenceTable
{
    /// <summary>The number sequence table's name, in the Module's schema.</summary>
    public const string Name = "pouryakit_number_sequences";

    extension(MigrationBuilder migrationBuilder)
    {
        /// <summary>Creates the number sequence table in <paramref name="schema"/>, the Module's own.</summary>
        public void CreateNumberSequenceTable(string schema) =>
            migrationBuilder.CreateTable(
                name: Name,
                schema: schema,
                columns: table => new
                {
                    scope_key = table.Column<string>(type: "text", nullable: false),
                    last_value = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table => table.PrimaryKey($"pk_{Name}", sequence => sequence.scope_key)
            );

        /// <summary>Drops the number sequence table from <paramref name="schema"/>, the Module's own.</summary>
        public void DropNumberSequenceTable(string schema) => migrationBuilder.DropTable(name: Name, schema: schema);
    }

    // The table in the DbContext's Module schema. The name comes from the model, never from input.
    internal static string In(DbContext dbContext) =>
        dbContext.Model.GetDefaultSchema() is { } schema
            ? $"\"{schema.Replace("\"", "\"\"", StringComparison.Ordinal)}\".{Name}"
            : Name;
}
