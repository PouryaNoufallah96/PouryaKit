using M2.Host;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Persistence;
using Wolverine.RDBMS;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace M2.Tests;

// Item 1: the Kit's PersistMessagesWithPostgresql(..., "wolverine") and Marten's IntegrateWithWolverine each register a
// main message store; Wolverine keeps one per store Uri, and refuses two mains at start.
public sealed class MessageStoreTests(Postgres postgres) : SpikeTest(postgres)
{
    [Fact]
    public async Task One_message_store_and_one_durability_agent_serve_both_write_models()
    {
        var host = await StartAsync();
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        Assert.Equal(2, host.Services.GetServices<IMessageStore>().Count());
        Assert.Single(await runtime.Stores.FindAllAsync());
        Assert.Single(await runtime.Stores.AllKnownAgentsAsync());
        Assert.Equal("wolverine", ((IMessageDatabase)runtime.Storage).SchemaName);
    }

    [Fact]
    public async Task A_marten_integration_with_its_envelopes_in_another_schema_fails_the_host_at_start()
    {
        var host = Build(new SpikeHostOptions { MessageStoreSchema = "marten_wolverine" });

        var refused = await Assert.ThrowsAsync<InvalidWolverineStorageConfigurationException>(() => host.StartAsync(Token));
        Assert.Contains("exactly one message store tagged as the 'main' store", refused.Message, StringComparison.Ordinal);
    }

    // The rejected option, one honest attempt: two stores, reconciled with Wolverine's opt-in resolver. The Host starts
    // with two durability agents, and Wolverine's Marten outbox still writes into the main store's tables, so the second
    // store's agent polls tables nothing writes.
    [Fact]
    public async Task Two_stores_reconciled_into_one_main_run_two_durability_agents_and_the_marten_outbox_writes_to_the_main()
    {
        var accountId = Guid.CreateVersion7();
        var host = await StartAsync(
            new SpikeHostOptions
            {
                MessageStoreSchema = "marten_wolverine",
                Wolverine = wolverine =>
                    wolverine.Durability.ResolveMainStoreOnConflict = mains =>
                        mains.First(store => ((IMessageDatabase)store).SchemaName == "wolverine"),
            }
        );
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        await Ledger(host).SendAsync(new M2.Ledger.OpenAccountCommand(accountId, Guid.CreateVersion7()), Context(), Token);

        Assert.Equal(2, (await runtime.Stores.FindAllAsync()).Count);
        Assert.Equal(2, (await runtime.Stores.AllKnownAgentsAsync()).Count);
        await WaitUntilAsync("select count(*) = 1 from wolverine.wolverine_incoming_envelopes where received_at = 'local://ledger.welcome/'");
        Assert.Equal(0, await CountAsync("select count(*) from marten_wolverine.wolverine_incoming_envelopes"));
    }
}
