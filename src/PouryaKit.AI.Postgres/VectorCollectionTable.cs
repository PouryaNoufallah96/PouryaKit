using Microsoft.EntityFrameworkCore.Migrations;

namespace PouryaKit.AI.Postgres;

/// <summary>
/// The tables a Host keeps its vector collections in, one per collection, in the package's own schema
/// <see cref="Schema"/> beside the Modules' data, so a stored embedding commits with the Operation that stored it. A
/// row is keyed by tenant, stored as <c>OperationContext.NoTenant</c> for an Operation without one, and by the
/// application's key, and holds the stored text and its embedding as a pgvector <c>vector</c>. A collection is not part
/// of a Module's model: a migration of the Module that stores in it creates it with <see cref="CreateVectorCollection"/>,
/// once.
/// </summary>
public static class VectorCollectionTable
{
    /// <summary>The package's schema, which holds every vector collection.</summary>
    public const string Schema = "ai";

    // Taking the extension where a DBA already installed it needs no privilege, but its schema must be on the role's
    // search_path, as public, the default, is: the vector type here and the search's ::vector cast are unqualified, so an
    // extension in another schema fails with PostgreSQL's type "vector" does not exist, not this message. Creating it
    // needs a privilege and the server's pgvector, and either failure stops the migration naming both fixes.
    private const string CreateExtension = """
        do $$
        begin
            create extension if not exists vector;
        exception when others then
            raise exception using
                message = 'PouryaKit.AI.Postgres stores embeddings with the vector extension, which this database could not create. Install pgvector on the PostgreSQL server, or have a role that may create extensions run create extension vector in this database, then migrate again.',
                detail = sqlerrm;
        end
        $$;
        """;

    extension(MigrationBuilder migrationBuilder)
    {
        /// <summary>
        /// Creates the pgvector extension where the database has none, the schema <see cref="Schema"/> and the
        /// collection <paramref name="name"/> in it, whose embeddings have <paramref name="dimensions"/> dimensions, the
        /// number the Host's embedding model returns. The name is a lower-case ASCII letter, then up to 59 lower-case ASCII
        /// letters, digits and underscores. The migration fails, naming the fix, when the database has no vector
        /// extension and cannot create it. An installed extension is used where it is, and its schema must be on the Host
        /// role's <c>search_path</c>, as <c>public</c>, the default, is: the column's <c>vector</c> type and the search's
        /// <c>::vector</c> cast are unqualified, so with the extension in another schema the migration, or a search,
        /// fails with PostgreSQL's <c>type "vector" does not exist</c> instead.
        /// </summary>
        public void CreateVectorCollection(string name, int dimensions)
        {
            VectorCollection.ThrowIfInvalid(name);
            ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 1);
            migrationBuilder.Sql(CreateExtension);
            migrationBuilder.EnsureSchema(Schema);
            migrationBuilder.CreateTable(
                name: name,
                schema: Schema,
                columns: table => new
                {
                    tenant = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    embedding = table.Column<float[]>(
                        type: $"vector({dimensions})",
                        nullable: false
                    ),
                },
                constraints: table =>
                    table.PrimaryKey($"pk_{name}", row => new { row.tenant, row.key })
            );
        }

        /// <summary>Drops the collection <paramref name="name"/> from <see cref="Schema"/>.</summary>
        public void DropVectorCollection(string name)
        {
            VectorCollection.ThrowIfInvalid(name);
            migrationBuilder.DropTable(name: name, schema: Schema);
        }
    }
}
