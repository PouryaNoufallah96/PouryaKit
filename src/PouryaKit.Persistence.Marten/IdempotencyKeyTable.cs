using Weasel.Postgresql.Tables;

namespace PouryaKit.Persistence.Marten;

// A Stream Module's key table, of the shape CreateIdempotencyTable gives an EF Module's, created by Marten's own schema
// management with the store when the Host starts.
internal static class IdempotencyKeyTable
{
    public static Table In(string schema)
    {
        var table = new Table($"{schema}.{IdempotencyStatements.Table}") { PrimaryKeyName = $"pk_{IdempotencyStatements.Table}" };
        foreach (var key in (string[])["tenant", "actor_kind", "actor_id", "command", "request_id"])
        {
            table.AddColumn(key, "text").AsPrimaryKey();
        }

        table.AddColumn("response", "text");
        table.AddColumn("reserved_at", "timestamp with time zone").NotNull();
        table.Indexes.Add(new IndexDefinition($"ix_{IdempotencyStatements.Table}_reserved_at").AgainstColumns("reserved_at"));
        return table;
    }
}
