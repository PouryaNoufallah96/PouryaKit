using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using PouryaKit.Cqrs;

namespace PouryaKit.Persistence.EFCore;

/// <summary>
/// The table a Module keeps the keys of its <c>[Idempotent]</c> commands in, in its own schema beside its data, so a key
/// and its recorded success commit with the command's effect or not at all. It is not part of the Module's model: a
/// migration creates it with <see cref="CreateIdempotencyTable"/>, once, in every Module with a command that opts in.
/// A row is keyed by tenant, Actor kind and id, the command's full type name with its assembly's simple name
/// (<c>Namespace.Type, Assembly</c>) and the request id; a null tenant is
/// stored as <see cref="NoTenant"/>. Its response is the command's success value as JSON, null for a command without
/// one, and its <c>reserved_at</c> the Host's time when the key was reserved. The Host removes a row once it is older
/// than <see cref="PouryaKitIdempotencyOptions.Retention"/>.
/// </summary>
public static class IdempotencyTable
{
    /// <summary>The idempotency key table's name, in the Module's schema.</summary>
    public const string Name = IdempotencyStatements.Table;

    /// <summary>The tenant column of an Operation without a tenant: <see cref="OperationContext.NoTenant"/>.</summary>
    public const string NoTenant = OperationContext.NoTenant;

    extension(MigrationBuilder migrationBuilder)
    {
        /// <summary>
        /// Creates the idempotency key table in <paramref name="schema"/>, the Module's own, with the index on
        /// <c>reserved_at</c> its cleanup reads. A PostgreSQL table, as the store and the cleanup are PostgreSQL-only:
        /// <c>reserved_at</c> is a <c>timestamp with time zone</c>, the store writes with <c>on conflict do nothing</c>,
        /// and the cleanup, which <c>AddPouryaKitEfCore</c> installs in every Host, deletes by <c>ctid</c> and skips,
        /// through <c>to_regclass</c>, every Module schema without this table.
        /// </summary>
        public void CreateIdempotencyTable(string schema)
        {
            migrationBuilder.CreateTable(
                name: Name,
                schema: schema,
                columns: table => new
                {
                    tenant = table.Column<string>(type: "text", nullable: false),
                    actor_kind = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    command = table.Column<string>(type: "text", nullable: false),
                    request_id = table.Column<string>(type: "text", nullable: false),
                    response = table.Column<string>(type: "text", nullable: true),
                    reserved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                    table.PrimaryKey(
                        $"pk_{Name}",
                        key => new
                        {
                            key.tenant,
                            key.actor_kind,
                            key.actor_id,
                            key.command,
                            key.request_id,
                        }
                    )
            );
            migrationBuilder.CreateIndex(name: $"ix_{Name}_reserved_at", schema: schema, table: Name, column: "reserved_at");
        }

        /// <summary>Drops the idempotency key table from <paramref name="schema"/>, the Module's own.</summary>
        public void DropIdempotencyTable(string schema) => migrationBuilder.DropTable(name: Name, schema: schema);
    }

    // The table in the DbContext's Module schema. The name comes from the model, never from input.
    internal static string In(DbContext dbContext) =>
        dbContext.Model.GetDefaultSchema() is { } schema
            ? $"\"{schema.Replace("\"", "\"\"", StringComparison.Ordinal)}\".{Name}"
            : Name;
}
