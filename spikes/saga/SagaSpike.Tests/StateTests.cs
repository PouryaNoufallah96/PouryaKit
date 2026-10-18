using SagaSpike.Host;

namespace SagaSpike.Tests;

// Item 2: saga state lives in the Kit's message store schema, here one of its own, kitstore, with the step claims beside
// it; and each step's state, claim and emissions commit in one transaction. Read between the step's commit and its
// return, before Wolverine sends what the step emitted or marks its envelope handled.
public sealed class StateTests(Postgres postgres) : SpikeTest(postgres)
{
    [Fact]
    public async Task Saga_state_and_its_step_claims_are_rows_of_the_message_store_schema_committed_with_the_steps_emissions()
    {
        List<(string Input, int Rows, int Instants)> commits = [];
        var host = await StartAsync(
            new SpikeHostOptions
            {
                StoreSchema = "kitstore",
                Sagas = sagas => sagas.AfterCommit = async (wireName, id) =>
                {
                    if (wireName == "orders.payment-received.v1")
                    {
                        // Every row the step wrote: the saga's state, unless the step completed it, the step's claim, and
                        // each envelope it stored, an emission among them.
                        var rows = await Db.RowsAsync(
                            ConnectionString,
                            """
                            select pg_xact_commit_timestamp(xmin) from kitstore.fulfilmentsaga_saga
                            union all select pg_xact_commit_timestamp(xmin) from kitstore.pouryakit_saga_steps where input = 'orders.payment-received.v1' and event_id = $1
                            union all select pg_xact_commit_timestamp(xmin) from kitstore.wolverine_incoming_envelopes where received_at = 'local://billing.order-fulfilled/'
                            """,
                            id
                        );
                        commits.Add((wireName, rows.Count, rows.Select(row => row[0]).Distinct().Count()));
                    }
                },
            }
        );
        var order = Guid.CreateVersion7();
        var sagaId = $"tenant-a:{order}";

        await PlaceAsync(host, Context("tenant-a"), order, installments: 2);
        await WaitUntilAsync("select count(*) = 1 from kitstore.fulfilmentsaga_saga where id = $1", sagaId);
        await PayAsync(host, Context("tenant-a"), order);
        await WaitUntilAsync("select (body->>'Paid')::int = 1 from kitstore.fulfilmentsaga_saga where id = $1", sagaId);
        await SettledAsync("kitstore");

        Assert.Equal(["kitstore"], (await Db.RowsAsync(ConnectionString, "select table_schema from information_schema.tables where table_name = 'fulfilmentsaga_saga'")).Select(row => (string)row[0]!));
        Assert.Equal(["kitstore"], (await Db.RowsAsync(ConnectionString, "select table_schema from information_schema.tables where table_name = 'pouryakit_saga_steps'")).Select(row => (string)row[0]!));
        Assert.Equal(3, await CountAsync("select count(*) from kitstore.pouryakit_saga_steps where saga = $1 and saga_id = $2", Fulfilment, sagaId));
        // The first payment's commit: the state and the claim, one instant.
        Assert.Equal(("orders.payment-received.v1", 2, 1), commits[0]);

        await PayAsync(host, Context("tenant-a"), order);
        await WaitUntilAsync("select count(*) = 1 from billing.pouryakit_receipts where subscription = 'billing.order-fulfilled'");

        // The second, which completed the saga: its state deleted, its claim and its emission, one instant.
        Assert.Equal(("orders.payment-received.v1", 2, 1), commits[1]);
        Assert.Null(await SagaAsync(sagaId, "kitstore"));
    }
}
