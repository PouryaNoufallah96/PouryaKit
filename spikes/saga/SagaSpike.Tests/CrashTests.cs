using SagaSpike.Host;

namespace SagaSpike.Tests;

// Item 6: a child process runs a payment step and is killed (SIGKILL), then a Host started on the same store recovers
// the payment's envelope, which the dead node still owned, and delivers it again.
public sealed class CrashTests(Postgres postgres) : SpikeTest(postgres)
{
    private const string Payments = "SagaInputFulfilmentSaga_PaymentReceivedV1";

    // Killed after the step's commit, before the step returned and Wolverine marked its envelope handled, a mark
    // Wolverine's lightweight saga storage does not make in the step's transaction: the redelivery finds the step's claim.
    [Fact]
    public async Task A_step_committed_before_its_process_is_killed_is_not_applied_again_after_restart()
    {
        var order = Guid.CreateVersion7();
        var child = await Child.StartUntilAsync("COMMITTED", "crash-after-commit", ConnectionString, "tenant-a", order.ToString());
        await Child.KillAsync(child);
        Assert.Equal(1, await PaidAsync($"tenant-a:{order}"));
        Assert.Equal(1, await CountAsync("select count(*) from wolverine.wolverine_incoming_envelopes where message_type = $1 and status = 'Incoming' and owner_id <> 0", Payments));

        await StartAsync();

        await WaitUntilAsync("select count(*) = 1 from wolverine.wolverine_incoming_envelopes where message_type = $1 and status = 'Handled'", Payments);
        await SettledAsync();
        Assert.Equal(1, await PaidAsync($"tenant-a:{order}"));
        Assert.Equal(1, await CountAsync("select count(*) from wolverine.pouryakit_saga_steps where input = 'orders.payment-received.v1'"));
    }

    // Killed inside the step, before its commit. The Kit's claim is in the step's transaction, rolled back with it, so the
    // redelivery applies the step. Wolverine's logical deduplication, the rejected option, claims on a connection of its
    // own before the step and gives the claim back only when the step fails in a live process: the redelivery is refused
    // as a duplicate of a step that never committed, and the payment is lost.
    [Theory]
    [InlineData("kit", 1)]
    [InlineData("wolverine", 0)]
    public async Task A_step_whose_process_is_killed_before_it_commits(string deduplication, int paidAfterRestart)
    {
        var order = Guid.CreateVersion7();
        var child = await Child.StartUntilAsync("IN STEP", "crash-in-step", ConnectionString, "tenant-a", order.ToString(), deduplication);
        await Child.KillAsync(child);
        Assert.Equal(0, await PaidAsync($"tenant-a:{order}"));
        // What survived the kill: Wolverine's claim, committed before the step, or no Kit claim, rolled back with it.
        Assert.Equal(
            deduplication == "wolverine" ? 1 : 0,
            deduplication == "wolverine"
                ? await CountAsync("select count(*) from wolverine.wolverine_deduplication_hashed")
                : await CountAsync("select count(*) from wolverine.pouryakit_saga_steps where input = 'orders.payment-received.v1'")
        );

        await StartAsync(deduplication == "wolverine" ? SpikeOptions.WolverineDeduplication : null);

        await WaitUntilAsync("select count(*) = 1 from wolverine.wolverine_incoming_envelopes where message_type = $1 and status = 'Handled'", Payments);
        await SettledAsync();
        Assert.Equal(paidAfterRestart, await PaidAsync($"tenant-a:{order}"));
    }
}
