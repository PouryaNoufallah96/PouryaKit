using Inventory;
using Inventory.StockCounts.Features.StartStockCount;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence.EFCore;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

public sealed class NumberSequenceTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private static readonly Error Refused = Error.Conflict("StockCount.Refused", "The stock count was refused after its number.");

    [Fact(Timeout = 60_000)]
    public async Task Two_operations_on_one_scope_key_get_consecutive_numbers_and_the_second_waits_for_the_first_to_commit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Hold first = new();
        long firstNumber = 0;
        var firstRun = RunInInventoryAsync(
            new HoldChangeCommand(
                async (dispatcher, ct) =>
                {
                    var started = await dispatcher.SendAsync(new StartStockCountCommand(), ct);
                    firstNumber = started.Value;
                    return started;
                },
                first
            ),
            cancellationToken
        );
        await first.Changed.Task.WaitAsync(cancellationToken);

        var secondRun = Fixture.Gateway<InventoryModule>().SendAsync(new StartStockCountCommand(), cancellationToken);
        await Fixture.WaitUntilAsync(
            "exists (select from pg_stat_activity where wait_event_type = 'Lock' and query like '%pouryakit_number_sequences%')",
            "The second stock count did not wait on the first's number sequence row."
        );
        Assert.False(secondRun.IsCompleted);

        first.Released.SetResult();
        Assert.True((await firstRun).IsSuccess);
        var second = await secondRun;

        Assert.Equal(1, firstNumber);
        Assert.Equal(2, second.Value);
        Assert.Equal([[1L], [2L]], await Fixture.RowsAsync("select number from inventory.stock_counts order by number"));
    }

    [Fact]
    public async Task A_rolled_back_operation_gives_its_number_back_to_the_next_operation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Hold held = new();
        held.Released.SetResult();
        long released = 0;

        var failed = await RunInInventoryAsync(
            new HoldChangeCommand(
                async (dispatcher, ct) =>
                {
                    released = (await dispatcher.SendAsync(new StartStockCountCommand(), ct)).Value;
                    return Refused;
                },
                held
            ),
            cancellationToken
        );
        var next = await Fixture.Gateway<InventoryModule>().SendAsync(new StartStockCountCommand(), cancellationToken);

        Assert.Equal(Refused, Assert.Single(failed.Errors));
        Assert.Equal(1, released);
        Assert.Equal(released, next.Value);
        Assert.Equal([[1L]], await Fixture.RowsAsync("select number from inventory.stock_counts"));
    }

    [Fact(Timeout = 60_000)]
    public async Task Two_operations_that_deadlock_on_two_scope_keys_commit_once_and_the_other_fails_with_a_conflict()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Hold northFirst = new(), southFirst = new();
        var northRun = RunInInventoryAsync(new TakeNumbersCommand(["north", "south"], northFirst), cancellationToken);
        var southRun = RunInInventoryAsync(new TakeNumbersCommand(["south", "north"], southFirst), cancellationToken);
        await Task.WhenAll(northFirst.Changed.Task, southFirst.Changed.Task).WaitAsync(cancellationToken);

        northFirst.Released.SetResult();
        southFirst.Released.SetResult();
        Result[] results = await Task.WhenAll(northRun, southRun);

        Assert.Single(results, result => result.IsSuccess);
        Assert.Equal(UnitOfWorkErrors.ConcurrencyConflict, Assert.Single(Assert.Single(results, result => result.IsFailure).Errors));
        Assert.Equal(
            [["north", 1L], ["south", 1L]],
            await Fixture.RowsAsync("select scope_key, last_value from inventory.pouryakit_number_sequences order by scope_key")
        );
    }

    [Fact]
    public async Task A_module_without_the_table_is_told_to_add_the_migration_that_creates_it()
    {
        Hold none = new();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Fixture.Gateway<StubModule>().SendAsync(new TakeNumbersCommand(["any"], none), TestContext.Current.CancellationToken)
        );

        Assert.Equal(
            "StubDbContext has no \"stub\".pouryakit_number_sequences table to keep its number sequences in. Add a migration to the Module that calls CreateNumberSequenceTable with its schema.",
            refused.Message
        );
    }

    // Begun in a scope as an Inventory Operation, so the command runs in Inventory's Unit of Work.
    private async Task<Result> RunInInventoryAsync(Command command, CancellationToken cancellationToken)
    {
        // Yields first, so the caller goes on before this Operation reaches a hold or a lock.
        await Task.Yield();
        await using var scope = Fixture.Host.Services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(new OperationContext(GatewayTestExtensions.TestActor), typeof(InventoryModule));
        return await scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync(command, cancellationToken);
    }
}
