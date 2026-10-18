namespace PouryaKit.Messaging.Wolverine.Sagas;

// The SQL of a step's claim, in the shape of ReceiptStatements: PostgreSQL statements whose values are composite-format
// items ({0}, {1}, …) bound as parameters, over a table name the Kit gives, never input. Feature code never runs them.
internal static class SagaStepStatements
{
    // The claims table, in the message store's schema beside Wolverine's saga tables.
    public const string Table = "pouryakit_saga_steps";

    // Claims the step of the saga {0} for the input of wire name {1} and identity {2}, of the saga identity {3}. The
    // primary key makes a second claim of one step wait for the first transaction, then insert nothing.
    public static string Claim(string table) =>
        $"insert into {table} (saga, input, event_id, saga_id) values ({{0}}, {{1}}, {{2}}, {{3}}) on conflict do nothing";

    // Creates the claims table in the store's schema {0}, a quoted identifier, under the advisory lock {1} Wolverine takes
    // to migrate the store, held to the transaction's end, so instances that start at once create it once. Claims are
    // never deleted: a late copy of a starting event would otherwise start its saga again.
    public const string CreateTable = $$"""
        select pg_advisory_xact_lock({1});
        create schema if not exists {0};
        create table if not exists {0}.{{Table}} (
            saga text not null,
            input text not null,
            event_id uuid not null,
            saga_id text not null,
            claimed_at timestamp with time zone not null default now(),
            constraint pk_{{Table}} primary key (saga, input, event_id)
        );
        """;
}
