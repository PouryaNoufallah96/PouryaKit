namespace PouryaKit.Persistence;

/// <summary>
/// The SQL every Unit of Work's idempotency store runs on its Module's key table, for a persistence package's store and
/// cleanup: PostgreSQL statements whose values are composite-format items (<c>{0}</c>, <c>{1}</c>, …) the package binds as
/// parameters its own way, over a table name its registration gives, never input. A key is its tenant (<c>{0}</c>), Actor
/// kind (<c>{1}</c>) and id (<c>{2}</c>), command (<c>{3}</c>) and request id (<c>{4}</c>). Feature code never runs them.
/// </summary>
public static class IdempotencyStatements
{
    /// <summary>The key table's name, in the Module's schema.</summary>
    public const string Table = "pouryakit_idempotency_keys";

    private const string Key = "tenant = {0} and actor_kind = {1} and actor_id = {2} and command = {3} and request_id = {4}";

    /// <summary>
    /// Reserves the key, with <c>{5}</c> the time it is reserved at: one row when reserved, none when another transaction
    /// holds it, after waiting for that transaction to end.
    /// </summary>
    public static string Reserve(string table) =>
        $"insert into {table} (tenant, actor_kind, actor_id, command, request_id, reserved_at) values ({{0}}, {{1}}, {{2}}, {{3}}, {{4}}, {{5}}) on conflict do nothing";

    /// <summary>The key's recorded success as the column <c>Value</c>, null for a command without one; no row when it is gone.</summary>
    public static string Read(string table) => $"select response as \"Value\" from {table} where {Key}";

    /// <summary>Records the key's success, <c>{5}</c>: one row when the key is reserved.</summary>
    public static string Record(string table) => $"update {table} set response = {{5}} where {Key}";

    /// <summary>Removes at most <c>{1}</c> keys reserved before <c>{0}</c>, one batch of a cleanup.</summary>
    public static string RemoveExpired(string table) =>
        $"delete from {table} where ctid in (select ctid from {table} where reserved_at < {{0}} limit {{1}})";
}
