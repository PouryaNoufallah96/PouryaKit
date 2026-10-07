using Microsoft.EntityFrameworkCore.Migrations;

namespace PouryaKit.Persistence.EFCore;

/// <summary>
/// The table a Module keeps the keys of its <c>[Idempotent]</c> commands in, in its own schema beside its data, so a key
/// and its recorded success commit with the command's effect or not at all. It is not part of the Module's model: a
/// migration creates it with <see cref="CreateIdempotencyTable"/>, once, in every Module with a command that opts in.
/// A row is keyed by tenant, Actor kind and id, the command's full type name and the request id; a null tenant is
/// stored as <see cref="NoTenant"/>. Its response is the command's success value as JSON, null for a command without
/// one. Rows are never removed by the Kit.
/// </summary>
public static class IdempotencyTable
{
    public const string Name = "pouryakit_idempotency_keys";

    /// <summary>
    /// The tenant column of an Operation without a tenant: the empty string, which no tenant can be, since an
    /// <c>OperationContext</c> refuses a blank one. A Host without tenants keeps one stable key per Actor, command and
    /// request id.
    /// </summary>
    public const string NoTenant = "";

    extension(MigrationBuilder migrationBuilder)
    {
        /// <summary>Creates the idempotency key table in <paramref name="schema"/>, the Module's own.</summary>
        public void CreateIdempotencyTable(string schema) =>
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

        public void DropIdempotencyTable(string schema) => migrationBuilder.DropTable(name: Name, schema: schema);
    }
}
