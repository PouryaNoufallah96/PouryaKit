using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime;

namespace S1.Tests;

/// <summary>
/// Review finding R1 and R2: a Notification that is committed but never flushed, on a Host that stays alive,
/// and an <c>IDbContextOutbox&lt;T&gt;</c> instance used for more than one commit attempt.
/// </summary>
public sealed class StrandedNotificationTests(PostgresServer server)
{
    private static readonly TimeSpan StrandedQuietPeriod = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Notification_committed_but_not_flushed_waits_on_a_live_host_until_the_host_stops()
    {
        await using var db = await server.CreateDatabaseAsync();
        var id = Guid.NewGuid();

        using (var host = db.BuildHost())
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            var node = NodeOf(host);

            await Assert.ThrowsAsync<InvalidOperationException>(() => new ModuleDriver(host, db, ModuleName.Catalog)
                .RunAsync(id, hooks: new CommitHooks { AfterCommitBeforeFlush = () => throw new InvalidOperationException("failure after commit") }));

            // Committed, owned by a live node, never flushed: no recovery path picks it up while the node lives.
            await Task.Delay(StrandedQuietPeriod);
            Assert.Equal(1, await db.ProductsAsync(id));
            Assert.Equal(0, await db.DeliveriesAsync(id));
            Assert.Equal(("Incoming", node), Single(await db.IncomingEnvelopesAsync()));

            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        // A graceful stop releases the node's inbox rows (owner 0); the next Host delivers.
        Assert.Equal(("Incoming", 0), Single(await db.IncomingEnvelopesAsync()));
        using var next = db.BuildHost();
        await next.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(await Eventually.WaitAsync(async () => await db.DeliveriesAsync(id) == 1, DeliveryBudget));
    }

    [Fact]
    public async Task Notification_committed_but_not_flushed_is_delivered_on_the_same_live_host_when_inbox_stale_time_is_set()
    {
        var staleTime = TimeSpan.FromSeconds(5);
        await using var db = await server.CreateDatabaseAsync();
        using var host = db.BuildHost(inboxStaleTime: staleTime);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var id = Guid.NewGuid();
        var clock = new Stopwatch();

        await Assert.ThrowsAsync<InvalidOperationException>(() => new ModuleDriver(host, db, ModuleName.Catalog)
            .RunAsync(id, hooks: new CommitHooks
            {
                AfterCommitBeforeFlush = () =>
                {
                    clock.Start();
                    throw new InvalidOperationException("failure after commit");
                },
            }));

        Assert.True(await Eventually.WaitAsync(async () => await db.DeliveriesAsync(id) == 1, DeliveryBudget),
            "the stranded Notification was not delivered on the live Host");
        var elapsed = clock.Elapsed;
        TestContext.Current.TestOutputHelper?.WriteLine($"InboxStaleTime={staleTime.TotalSeconds}s delivered {elapsed.TotalSeconds:F1}s after commit");
        Assert.True(elapsed >= staleTime - TimeSpan.FromSeconds(1), $"delivered after {elapsed}, before the stale time");
        await Task.Delay(QuietPeriod);
        Assert.Equal(1, await db.DeliveriesAsync(id));
    }

    [Fact]
    public async Task Inbox_stale_time_shorter_than_a_running_handler_hands_the_notification_out_again()
    {
        await using var db = await server.CreateDatabaseAsync();
        using var host = db.BuildHost(inboxStaleTime: TimeSpan.FromSeconds(3));
        await host.StartAsync(TestContext.Current.CancellationToken);
        var id = Guid.NewGuid();
        var clock = Stopwatch.StartNew();

        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SpikeUnitOfWork<CatalogDbContext>>().ExecuteAsync(async (ctx, outbox) =>
            {
                ctx.Products.Add(new Product { Id = id, Name = "slow" });
                await outbox.PublishAsync(new SlowProductCreated(id, HandlerSeconds: 15));
                return Outcome.Succeeded;
            });
        }

        // The first run is still inside its 15 s handler when the row is released and handed out again.
        Assert.True(await Eventually.WaitAsync(async () => await db.DeliveriesAsync(id) == 2, TimeSpan.FromSeconds(14)),
            "the in-flight Notification was not handed out a second time");
        TestContext.Current.TestOutputHelper?.WriteLine($"second delivery {clock.Elapsed.TotalSeconds:F1}s after commit");
    }

    [Fact]
    public async Task An_outbox_instance_reused_after_a_rollback_sends_the_rolled_back_notification_with_the_next_commit()
    {
        await using var db = await server.CreateDatabaseAsync();
        using var host = db.BuildHost();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var (rolledBack, committed) = (Guid.NewGuid(), Guid.NewGuid());

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<SpikeUnitOfWork<CatalogDbContext>>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => uow.ExecuteAsync(CreateProduct(rolledBack),
                new CommitHooks { AfterSaveBeforeCommit = _ => throw new InvalidOperationException("failure after save") }));
            uow.DbContext.ChangeTracker.Clear(); // isolate the outbox: the rolled-back entity is not re-saved

            await uow.ExecuteAsync(CreateProduct(committed));
        }

        Assert.True(await Eventually.WaitAsync(async () => await db.DeliveriesAsync(committed) == 1, DeliveryBudget));
        Assert.True(await Eventually.WaitAsync(async () => await db.DeliveriesAsync(rolledBack) == 1, QuietPeriod),
            "the rolled-back Notification was not sent by the later flush");
        Assert.Equal(0, await db.ProductsAsync(rolledBack));
    }

    [Fact]
    public async Task A_second_commit_on_an_outbox_instance_that_already_flushed_is_committed_but_not_sent()
    {
        await using var db = await server.CreateDatabaseAsync();
        using var host = db.BuildHost();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var node = NodeOf(host);
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<SpikeUnitOfWork<CatalogDbContext>>();
            await uow.ExecuteAsync(CreateProduct(first));
            await uow.ExecuteAsync(CreateProduct(second));
        }

        Assert.True(await Eventually.WaitAsync(async () => await db.DeliveriesAsync(first) == 1, DeliveryBudget));
        await Task.Delay(QuietPeriod);
        Assert.Equal(1, await db.ProductsAsync(second));
        Assert.Equal(0, await db.DeliveriesAsync(second));
        var stranded = await db.ScalarAsync<int>(
            "select owner_id from wolverine.wolverine_incoming_envelopes where status = 'Incoming'");
        Assert.Equal(node, stranded);
    }

    private static Func<CatalogDbContext, Wolverine.EntityFrameworkCore.IDbContextOutbox<CatalogDbContext>, Task<Outcome>> CreateProduct(Guid id) =>
        async (ctx, outbox) =>
        {
            ctx.Products.Add(new Product { Id = id, Name = "p" });
            await outbox.PublishAsync(new ProductCreated(id));
            return Outcome.Succeeded;
        };

    private static int NodeOf(IHost host) =>
        host.Services.GetRequiredService<IWolverineRuntime>().Options.Durability.AssignedNodeNumber;

    private static (string Status, int OwnerId) Single(List<EnvelopeRow> rows)
    {
        var row = Assert.Single(rows);
        return (row.Status, row.OwnerId);
    }
}
