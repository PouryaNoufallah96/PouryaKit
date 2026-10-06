using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Features.CreateProduct;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Domain;
using PouryaKit.Modularity;
using PouryaKit.Testing;
using Wolverine;
using Wolverine.Runtime;

namespace ModularMonolith.Tests;

/// <summary>
/// Two Modules subscribe to one Integration Event, each under its own subscription: what one does, fails or leaves
/// behind never runs the other again.
/// </summary>
public sealed class IndependentDeliveryTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private const string WireName = "catalog.product-created.v1";

    [Fact]
    public async Task A_product_created_once_is_published_once_and_inventory_and_billing_each_apply_it_once()
    {
        var product = Guid.CreateVersion7();

        var (_, deliveries) = await Fixture.WaitForDeliveriesAsync(
            () => CreateProductAsync(product),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            [new Delivery("billing.product-created", WireName, false), new Delivery("inventory.product-created", WireName, false)],
            deliveries.Where(delivery => delivery.WireName == WireName).OrderBy(delivery => delivery.Subscription, StringComparer.Ordinal)
        );
        // Catalog published it once: one Receipt of the publishing Notification Handler, one delivery per subscription.
        var published = Assert.Single(
            Assert.Single(
                await Fixture.RowsAsync(
                    "select message_id from catalog.pouryakit_receipts where subscription = 'catalog.product-created.publish-product-created'"
                )
            )
        );
        // Each Module applied it in its own data, with a Receipt of its own keyed by the same identity.
        Assert.Equal([[product]], await Fixture.RowsAsync("select product_id from inventory.stock_items"));
        Assert.Equal([[product]], await Fixture.RowsAsync("select product_id from billing.price_lists"));
        Assert.Equal(
            [["billing.product-created", published], ["inventory.product-created", published]],
            await Fixture.RowsAsync(
                "select subscription, message_id from billing.pouryakit_receipts union all select subscription, message_id from inventory.pouryakit_receipts where subscription = 'inventory.product-created' order by 1"
            )
        );
    }

    public static TheoryData<string, string> FailingAndSucceedingModules =>
        new() { { "inventory.product-created", "billing.product-created" }, { "billing.product-created", "inventory.product-created" } };

    // The failure is injected by the test at the delivery step, right after the failing subscription's Receipt is
    // recorded and before its handler runs: its transaction rolls back, Receipt included, as if the handler had thrown.
    // Neither Module's code knows the test.
    [Theory]
    [MemberData(nameof(FailingAndSucceedingModules))]
    public async Task A_module_whose_delivery_keeps_failing_never_runs_the_other_modules_handler_again(string failing, string succeeding)
    {
        var product = Guid.CreateVersion7();

        using (DeliveryProbes.FailAfterReceipt(failing))
        {
            await Fixture.WaitForDeliveriesAsync(() => CreateProductAsync(product), TestContext.Current.CancellationToken);
        }

        var published = await Fixture.ScalarAsync<Guid>(
            "select message_id from catalog.pouryakit_receipts where subscription = 'catalog.product-created.publish-product-created'"
        );
        // Every attempt of the failing subscription, through its retries to its dead letter; the other's one delivery.
        Assert.Equal(1 + TestDeliveryServiceCollectionExtensions.RetryDelays.Count, DeliveryProbes.AttemptsOf(failing, published).Count);
        Assert.Single(DeliveryProbes.AttemptsOf(succeeding, published));
        Assert.Equal(
            [failing],
            (await Fixture.DeadLettersAsync(TestContext.Current.CancellationToken)).Select(deadLetter => deadLetter.Subscription)
        );
        // The succeeding Module applied it once, with its Receipt; the failing one left neither effect nor Receipt.
        Assert.Equal((1L, 1L), await EffectAndReceiptsAsync(succeeding, product, published));
        Assert.Equal((0L, 0L), await EffectAndReceiptsAsync(failing, product, published));
    }

    private async Task<(long Effects, long Receipts)> EffectAndReceiptsAsync(string subscription, Guid product, Guid published)
    {
        var (module, table) = subscription.StartsWith("inventory", StringComparison.Ordinal)
            ? ("inventory", "stock_items")
            : ("billing", "price_lists");
        return (
            await Fixture.ScalarAsync<long>($"select count(*) from {module}.{table} where product_id = '{product}'"),
            await Fixture.ScalarAsync<long>(
                $"select count(*) from {module}.pouryakit_receipts where subscription = '{subscription}' and message_id = '{published}'"
            )
        );
    }

    public static TheoryData<string, string> FailingAndSucceeding =>
        new() { { Circulation.Archive, Circulation.Ledger }, { Circulation.Ledger, Circulation.Archive } };

    [Theory]
    [MemberData(nameof(FailingAndSucceeding))]
    public async Task A_subscription_that_keeps_failing_never_runs_the_one_that_succeeded_again(string failing, string succeeding)
    {
        var text = Unique();
        var circulated = new NoteToCirculateDomainEvent(text);
        Circulation.Fail(failing, text);

        await Fixture.WaitForDeliveriesAsync(
            () => CirculateAsync(Fixture.Gateway<StubModule>(), circulated),
            TestContext.Current.CancellationToken
        );

        // Through every retry of the failing one and its dead letter, the other was delivered once: not even handed
        // out again for its Receipt to stop.
        Assert.Equal(
            1 + TestDeliveryServiceCollectionExtensions.RetryDelays.Count,
            DeliveryProbes.AttemptsOf(failing, circulated.Id).Count
        );
        Assert.Single(DeliveryProbes.AttemptsOf(succeeding, circulated.Id));
        Assert.Single(Circulation.RunsOf(succeeding, text));
        Assert.Equal((1L, 0L), (await CopiesAsync(succeeding, text), await CopiesAsync(failing, text)));
    }

    [Fact]
    public async Task A_subscription_that_succeeded_is_not_run_again_after_its_host_dies_during_the_other_and_a_new_host_takes_over()
    {
        var text = Unique();
        var circulated = new NoteToCirculateDomainEvent(text);
        var dying = AppFixture.ComposeHost("Development", Fixture.ConnectionString, Fixture.Logs);
        await using var replacing = AppFixture.ComposeHost("Development", Fixture.ConnectionString, Fixture.Logs);
        await HostFixture.StartHostAsync(dying, TestContext.Current.CancellationToken);
        var hold = DeliveryProbes.HoldAfterReceipt(Circulation.Archive);
        try
        {
            await CirculateAsync(dying.Services.GetRequiredService<IGatewayProcessor<StubModule>>(), circulated);
            // The archive's delivery is inside its transaction, its Receipt written; the ledger's is handled.
            await hold.Held;
            await Fixture.WaitUntilAsync(
                $"exists (select 1 from wolverine.wolverine_incoming_envelopes where received_at = 'local://{Circulation.Ledger}/' and status = 'Handled')",
                "The ledger's delivery was never handled."
            );

            // The Host dies: its database sessions end, which rolls the held delivery back, and Wolverine's quick stop
            // neither drains nor gives up what the Host holds.
            Assert.Equal(
                [[true]],
                await Fixture.RowsAsync(
                    "select pg_terminate_backend(pid) from pg_stat_activity where state = 'idle in transaction' and query like '%pouryakit_receipts%'"
                )
            );
            ((WolverineRuntime)dying.Services.GetRequiredService<IWolverineRuntime>()).StopMode = StopMode.Quick;
            await dying.StopAsync(TestContext.Current.CancellationToken);
            Assert.Equal(
                [["Incoming", true]],
                await Fixture.RowsAsync(
                    $"select status, owner_id <> 0 from wolverine.wolverine_incoming_envelopes where received_at = 'local://{Circulation.Archive}/'"
                )
            );

            await HostFixture.StartHostAsync(replacing, TestContext.Current.CancellationToken);
            await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            // The held delivery never continues, as in a process that was killed.
            hold.Abandon();
            await dying.DisposeAsync();
        }

        // The ledger was delivered once. The archive was attempted in the dying Host, then recovered: by the new Host,
        // and sometimes in the same moment by the fixture's own Host, which runs on the same store as the same Solo
        // node; the Receipt stops the later of two such runs. Its handler ran once, outside the dying Host.
        Assert.Single(DeliveryProbes.AttemptsOf(Circulation.Ledger, circulated.Id));
        Assert.InRange(DeliveryProbes.AttemptsOf(Circulation.Archive, circulated.Id).Count, 2, 3);
        Assert.Single(Circulation.RunsOf(Circulation.Ledger, text));
        Assert.Single(Circulation.RunsOf(Circulation.Archive, text));
        Assert.Equal((1L, 1L), (await CopiesAsync(Circulation.Ledger, text), await CopiesAsync(Circulation.Archive, text)));
        Assert.Equal(
            [["Handled"], ["Handled"]],
            await Fixture.RowsAsync("select status from wolverine.wolverine_incoming_envelopes where message_type = 'stub.note-circulated.v1'")
        );
    }

    private static Task<Result> CirculateAsync(IGatewayProcessor<StubModule> stub, NoteToCirculateDomainEvent circulated) =>
        stub.SendAsync(new WriteNoteRaisingCommand(circulated.Text, [circulated]), TestContext.Current.CancellationToken);

    private Task<long> CopiesAsync(string subscription, string text) =>
        Fixture.ScalarAsync<long>($"select count(*) from stub.notes where text = '{Circulation.CopyOf(subscription, text)}'");

    private static string Unique() => $"note {Guid.CreateVersion7()}";

    private async Task<Result<Guid>> CreateProductAsync(Guid product)
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var category = Guid.CreateVersion7();
        await catalog.SendAsync(new CreateCategoryCommand(category, $"Lamps {category}"), TestContext.Current.CancellationToken);
        return await catalog.SendAsync(
            new CreateProductCommand(product, category, $"Lamp {product}"),
            TestContext.Current.CancellationToken
        );
    }
}
