using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Catalog;
using Catalog.Products.Features.RemoveProducts;
using Catalog.Products.Features.RenameProduct;
using Inventory;
using Inventory.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModularMonolith.Host;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;
using PouryaKit.Testing;
using RabbitMQ.Client;
using Wolverine;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.RabbitMQ;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports.Sending;

namespace ModularMonolith.Tests;

// Delivery between Hosts over RabbitMQ (ADR 0006's RabbitMQ topology amendment), each row of its invariants table: the
// Proving Sample's service composition, ModularMonolith.Host with a broker (Catalog, Billing and Ledger) and Inventory.Host, each
// a real Host with its own database and ServiceName, joined only by the broker.
[Collection<BrokerCollection>]
public sealed class RabbitMqDeliveryTests(BrokerFixture broker)
{
    private const string Audit = BrokerEnvironment.AuditSubscription;
    private const string StockItems = "inventory.product-created";
    private const string ProductCategories = "inventory.product-categories";
    private const string CatalogProducts = "inventory.catalog-products";
    private const string PublishProductCreated = "catalog.product-created.publish-product-created";
    private static readonly string HostQueue = $"rabbitmq://queue/{InventoryHost.ServiceName}";

    [Fact]
    public async Task A_publication_whose_only_subscription_is_in_another_host_is_written_in_its_commit_and_applied_there_once()
    {
        var environment = await broker.CreateEnvironmentAsync();
        using var inventory = await environment.StartInventoryHostAsync();
        await using var catalog = await environment.StartCatalogHostAsync();
        var product = Guid.CreateVersion7();
        List<object[]> written;

        // ProductCreatedV2's only subscription is Inventory's, in Inventory.Host. Held after the commit of the delivery
        // that published it, before anything is sent.
        using (
            var published = DeliveryProbes.HoldAfterCommit(
                typeof(CatalogModule),
                PublishProductCreated
            )
        )
        {
            await BrokerEnvironment.CreateProductAsync(catalog, product, "Lamp");
            await published.Held;
            written = await Sql.RowsAsync(
                environment.Catalog,
                """
                select o.destination, pg_xact_commit_timestamp(o.xmin) = pg_xact_commit_timestamp(r.xmin)
                from wolverine.wolverine_outgoing_envelopes o
                join catalog.pouryakit_receipts r on r.subscription = 'catalog.product-created.v2#published'
                where o.message_type = 'catalog.product-created.v2'
                """
            );
        }

        await environment.CreatedEventOfAsync(product);
        await BrokerEnvironment.WaitUntilAsync(
            environment.Inventory,
            $"exists (select from inventory.pouryakit_receipts where subscription = '{ProductCategories}')",
            "Inventory.Host never applied ProductCreatedV2."
        );
        await BrokerEnvironment.WaitUntilSettledAsync(environment.Inventory);
        await BrokerEnvironment.WaitUntilSettledAsync(environment.Catalog);

        Assert.Equal(
            [
                ["rabbitmq://exchange/catalog.product-created.v2", true],
            ],
            written
        );
        Assert.Equal(
            [
                [product],
            ],
            await Sql.RowsAsync(
                environment.Inventory,
                $"select c.product_id from inventory.product_categories c join inventory.pouryakit_receipts r on r.subscription = '{ProductCategories}' and pg_xact_commit_timestamp(r.xmin) = pg_xact_commit_timestamp(c.xmin)"
            )
        );
        // The Catalog Host publishes ProductCreatedV1 and receives it in Billing, from the publication itself: nothing it
        // publishes comes back to it from the broker.
        Assert.Equal(
            0L,
            await BrokerEnvironment.ScalarAsync<long>(
                environment.Catalog,
                "select count(*) from wolverine.wolverine_incoming_envelopes where received_at like 'rabbitmq:%'"
            )
        );
    }

    [Fact]
    public async Task A_failing_subscription_in_the_other_host_rolls_back_its_effect_is_dead_lettered_alone_and_its_replay_applies_once()
    {
        var environment = await broker.CreateEnvironmentAsync();
        using var inventory = await environment.StartInventoryHostAsync();
        await using var catalog = await environment.StartCatalogHostAsync();
        var product = Guid.CreateVersion7();

        // Each attempt of the audit writes its effect and then returns a failed Result.
        using (AuditProductIntegrationEventHandler.Refuse(product))
        {
            using (var audit = DeliveryProbes.HoldAfterReceipt(Audit))
            {
                await BrokerEnvironment.CreateProductAsync(catalog, product, "Lamp");
                await audit.Held;
                // While the audit's first attempt holds its uncommitted Receipt, Inventory's subscription commits its own.
                await BrokerEnvironment.WaitUntilAsync(
                    environment.Inventory,
                    $"exists (select from inventory.stock_items s join inventory.pouryakit_receipts r on r.subscription = '{StockItems}' and pg_xact_commit_timestamp(r.xmin) = pg_xact_commit_timestamp(s.xmin) where s.product_id = '{product}')",
                    "Inventory's subscription waited for the failing one."
                );
            }

            await BrokerEnvironment.WaitUntilAsync(
                environment.Inventory,
                "exists (select from wolverine.wolverine_dead_letters)",
                "The audit was never dead-lettered."
            );
        }

        var created = await environment.CreatedEventOfAsync(product);
        Assert.Equal(
            [
                [
                    $"local://{Audit}/",
                    "catalog.product-created.v1",
                    typeof(IntegrationEventHandlerFailedException).FullName!,
                ],
            ],
            await Sql.RowsAsync(
                environment.Inventory,
                "select received_at, message_type, exception_type from wolverine.wolverine_dead_letters"
            )
        );
        Assert.Equal(
            TestDeliveryServiceCollectionExtensions.RetryDelays.Count + 1,
            DeliveryProbes.AttemptsOf(Audit, created).Count
        );
        // Every attempt wrote its audit, and each rolled back with its Receipt.
        Assert.Equal(
            (0L, 0L),
            (await ReceiptsAsync(environment, Audit), await AuditsAsync(environment, product))
        );

        // Healed and replayed, the audit commits once with its Receipt; Inventory's own subscription never runs again.
        var deadLetters = inventory
            .Services.GetRequiredService<IWolverineRuntime>()
            .Storage.DeadLetters;
        var deadLetter = Assert.Single(
            (
                await deadLetters.QueryAsync(
                    new DeadLetterEnvelopeQuery { PageSize = 0 },
                    TestContext.Current.CancellationToken
                )
            ).Envelopes
        );
        await deadLetters.ReplayAsync(
            new DeadLetterEnvelopeQuery([deadLetter.Id]),
            TestContext.Current.CancellationToken
        );
        await BrokerEnvironment.WaitUntilAsync(
            environment.Inventory,
            $"exists (select from inventory.pouryakit_receipts where subscription = '{Audit}')",
            "The replayed audit never applied."
        );
        await BrokerEnvironment.WaitUntilSettledAsync(environment.Inventory);

        Assert.Equal(
            [
                [product],
            ],
            await AuditsWithTheirReceiptAsync(environment)
        );
        Assert.Single(DeliveryProbes.AttemptsOf(StockItems, created));
        Assert.Equal(1L, await StockItemsAsync(environment, product));
    }

    [Fact]
    public async Task A_copy_its_host_was_delivering_when_it_was_killed_is_delivered_after_it_restarts()
    {
        var environment = await broker.CreateEnvironmentAsync();
        var inventory = await environment.StartInventoryHostAsync();
        await using var catalog = await environment.StartCatalogHostAsync();
        var product = Guid.CreateVersion7();

        var audit = DeliveryProbes.HoldAfterReceipt(Audit);
        try
        {
            await BrokerEnvironment.CreateProductAsync(catalog, product, "Lamp");
            await audit.Held;
            // The broker envelope is marked handled: from here on the audit's copy is the only record of its delivery.
            await BrokerEnvironment.WaitUntilAsync(
                environment.Inventory,
                $"exists (select from wolverine.wolverine_incoming_envelopes where received_at = '{HostQueue}' and message_type = 'catalog.product-created.v1' and status = 'Handled')",
                "The broker envelope was never marked handled."
            );
            // A kill ends the held attempt's transaction, its Receipt uncommitted.
            await Sql.RowsAsync(
                environment.Inventory,
                "select pg_terminate_backend(pid) from pg_stat_activity where datname = current_database() and state = 'idle in transaction' and query like '%pouryakit_receipts%'"
            );
            await BrokerEnvironment.KillAsync(inventory);
        }
        finally
        {
            audit.Abandon();
        }

        using var restarted = await environment.StartInventoryHostAsync();
        await BrokerEnvironment.WaitUntilAsync(
            environment.Inventory,
            $"exists (select from inventory.pouryakit_receipts where subscription = '{Audit}')",
            "The copy the Host was delivering when it was killed was never delivered."
        );
        await BrokerEnvironment.WaitUntilSettledAsync(environment.Inventory);

        // The same copy, handed out again, not a second fan-out.
        Assert.Equal(
            [
                [$"local://{Audit}/", "Handled"],
            ],
            await Sql.RowsAsync(
                environment.Inventory,
                $"select received_at, status from wolverine.wolverine_incoming_envelopes where received_at = 'local://{Audit}/'"
            )
        );
        Assert.Equal(
            2,
            DeliveryProbes.AttemptsOf(Audit, await environment.CreatedEventOfAsync(product)).Count
        );
        Assert.Equal(
            [
                [product],
            ],
            await AuditsWithTheirReceiptAsync(environment)
        );
    }

    [Fact]
    public async Task A_publication_that_comes_back_through_a_binding_the_kit_never_makes_is_applied_once_and_never_sent_round_again()
    {
        var environment = await broker.CreateEnvironmentAsync();
        await using var catalog = await environment.StartCatalogHostAsync();
        var product = Guid.CreateVersion7();
        var queue = $"rabbitmq://queue/{ModularMonolithHost.ServiceName}";
        // An earlier deployment of the Catalog Host received ProductCreatedV1 from another Host, and the binding outlived it.
        await environment.BindAsync(ModularMonolithHost.ServiceName, "catalog.product-created.v1");
        var turnedAway = DeliveryProbes.TurnedAwayFrom("billing.product-created");

        await BrokerEnvironment.CreateProductAsync(catalog, product, "Lamp");
        var created = await environment.CreatedEventOfAsync(product);
        await BrokerEnvironment.WaitUntilAsync(
            environment.Catalog,
            $"exists (select from wolverine.wolverine_incoming_envelopes where received_at = '{queue}' and status = 'Handled')",
            "The publication never came back through the binding."
        );
        await BrokerEnvironment.WaitUntilSettledAsync(environment.Catalog);
        // Nothing more comes round afterwards.
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        await BrokerEnvironment.WaitUntilSettledAsync(environment.Catalog);

        Assert.Equal(
            [
                [queue, "catalog.product-created.v1", "Handled"],
            ],
            await Sql.RowsAsync(
                environment.Catalog,
                "select received_at, message_type, status from wolverine.wolverine_incoming_envelopes where received_at like 'rabbitmq:%'"
            )
        );
        Assert.Equal(
            0L,
            await BrokerEnvironment.ScalarAsync<long>(
                environment.Catalog,
                "select count(*) from wolverine.wolverine_dead_letters"
            )
        );
        Assert.Equal(0u, await environment.BrokerMessagesAsync(ModularMonolithHost.ServiceName));
        // Billing, beside Catalog, received it from the publication and once more from the binding, where its Receipt
        // stopped it: one price list.
        Assert.Equal(2, DeliveryProbes.AttemptsOf("billing.product-created", created).Count);
        Assert.Equal(turnedAway + 1, DeliveryProbes.TurnedAwayFrom("billing.product-created"));
        Assert.Equal(
            1L,
            await BrokerEnvironment.ScalarAsync<long>(
                environment.Catalog,
                $"select count(*) from billing.price_lists where product_id = '{product}'"
            )
        );
    }

    [Fact(Timeout = 120_000)]
    public async Task A_host_killed_between_storing_the_copies_and_marking_the_broker_envelope_handled_fans_out_again_and_applies_each_effect_once()
    {
        var environment = await broker.CreateEnvironmentAsync();
        var inventory = await environment.StartInventoryHostAsync();
        await using var catalog = await environment.StartCatalogHostAsync();
        var product = Guid.CreateVersion7();
        var turnedAway = DeliveryProbes.TurnedAwayFrom(StockItems);

        // The broker envelope's handled mark waits on a lock the test holds, so the Host dies in between.
        await using var holder = new Npgsql.NpgsqlConnection(environment.Inventory);
        await holder.OpenAsync(TestContext.Current.CancellationToken);
        await ExecuteAsync(
            holder,
            """
            create function public.hold_broker_mark() returns trigger language plpgsql as $$
            begin
                if new.status = 'Handled' and old.status <> 'Handled' and old.received_at like 'rabbitmq:%' then
                    perform pg_advisory_xact_lock(5151);
                end if;
                return new;
            end $$;
            create trigger hold_broker_mark before update on wolverine.wolverine_incoming_envelopes
                for each row execute function public.hold_broker_mark();
            select pg_advisory_lock(5151);
            """
        );
        await BrokerEnvironment.CreateProductAsync(catalog, product, "Lamp");
        // Every copy is stored and applied while each broker envelope still waits to be marked handled.
        await BrokerEnvironment.WaitUntilAsync(
            environment.Inventory,
            $"(select count(*) from inventory.pouryakit_receipts) = 4 and (select count(*) from pg_stat_activity where wait_event = 'advisory') > 0 and not exists (select from wolverine.wolverine_incoming_envelopes where received_at = '{HostQueue}' and status = 'Handled')",
            "The copies were not all applied before the broker envelopes were marked handled."
        );
        await BrokerEnvironment.KillAsync(inventory);
        await ExecuteAsync(
            holder,
            "select pg_terminate_backend(pid) from pg_stat_activity where wait_event = 'advisory'; drop trigger hold_broker_mark on wolverine.wolverine_incoming_envelopes; select pg_advisory_unlock(5151);"
        );

        using var restarted = await environment.StartInventoryHostAsync();
        await BrokerEnvironment.WaitUntilAsync(
            environment.Inventory,
            "(select count(*) from wolverine.wolverine_incoming_envelopes where received_at like 'local:%' and message_type like 'catalog.%') = 8",
            "The broker envelopes were not fanned out again."
        );
        await BrokerEnvironment.WaitUntilSettledAsync(environment.Inventory);

        Assert.Equal(
            [
                [$"local://{Audit}/", "Handled", 2L],
                [$"local://{CatalogProducts}/", "Handled", 2L],
                [$"local://{ProductCategories}/", "Handled", 2L],
                [$"local://{StockItems}/", "Handled", 2L],
                [HostQueue, "Handled", 3L],
            ],
            await Sql.RowsAsync(
                environment.Inventory,
                "select received_at, status, count(*) from wolverine.wolverine_incoming_envelopes where message_type like 'catalog.%' group by 1, 2 order by 1"
            )
        );
        Assert.Equal(
            [
                [Audit, 1L],
                [CatalogProducts, 1L],
                [ProductCategories, 1L],
                [StockItems, 1L],
            ],
            await Sql.RowsAsync(
                environment.Inventory,
                "select subscription, count(*) from inventory.pouryakit_receipts group by 1 order by 1"
            )
        );
        Assert.Equal(1L, await StockItemsAsync(environment, product));
        Assert.Equal(turnedAway + 1, DeliveryProbes.TurnedAwayFrom(StockItems));
    }

    [Fact(Timeout = 120_000)]
    public async Task A_copy_the_store_refuses_leaves_the_broker_envelope_unhandled_dead_lettered_at_the_host_queue_and_its_replay_applies_each_effect_once()
    {
        var environment = await broker.CreateEnvironmentAsync();
        using var inventory = await environment.StartInventoryHostAsync();
        await using var catalog = await environment.StartCatalogHostAsync();
        var product = Guid.CreateVersion7();

        // The store refuses the audit's copy of every envelope, and nothing else.
        await Sql.RowsAsync(
            environment.Inventory,
            $"""
            create function public.refuse_audit_copy() returns trigger language plpgsql as $$
            begin
                if new.received_at = 'local://{Audit}/' then
                    raise exception 'The store refuses the audit''s copy.';
                end if;
                return new;
            end $$;
            create trigger refuse_audit_copy before insert on wolverine.wolverine_incoming_envelopes
                for each row execute function public.refuse_audit_copy();
            """
        );
        await BrokerEnvironment.CreateProductAsync(catalog, product, "Lamp");
        var created = await environment.CreatedEventOfAsync(product);
        await BrokerEnvironment.WaitUntilAsync(
            environment.Inventory,
            $"exists (select from wolverine.wolverine_dead_letters) or exists (select from wolverine.wolverine_incoming_envelopes where received_at = '{HostQueue}' and message_type = 'catalog.product-created.v1' and status = 'Handled')",
            "ProductCreatedV1's broker envelope was neither dead-lettered nor marked handled."
        );
        await BrokerEnvironment.WaitUntilSettledAsync(environment.Inventory);

        // Never marked handled: after its retries the broker envelope is a dead letter at the Host's queue, the store's
        // refusal its cause, and the only record of the audit's delivery.
        Assert.Equal(
            [
                [HostQueue, "catalog.product-created.v1", "Npgsql.PostgresException"],
            ],
            await Sql.RowsAsync(
                environment.Inventory,
                "select received_at, message_type, exception_type from wolverine.wolverine_dead_letters"
            )
        );
        Assert.Equal(
            (0L, 0L),
            (
                await BrokerEnvironment.ScalarAsync<long>(
                    environment.Inventory,
                    $"select count(*) from wolverine.wolverine_incoming_envelopes where received_at in ('{HostQueue}', 'local://{Audit}/') and message_type = 'catalog.product-created.v1'"
                ),
                await ReceiptsAsync(environment, Audit)
            )
        );

        // With the store healed, the replay fans the envelope out again, and each subscription applies it once.
        await Sql.RowsAsync(
            environment.Inventory,
            "drop trigger refuse_audit_copy on wolverine.wolverine_incoming_envelopes; drop function public.refuse_audit_copy();"
        );
        var deadLetters = inventory
            .Services.GetRequiredService<IWolverineRuntime>()
            .Storage.DeadLetters;
        var deadLetter = Assert.Single(
            (
                await deadLetters.QueryAsync(
                    new DeadLetterEnvelopeQuery { PageSize = 0 },
                    TestContext.Current.CancellationToken
                )
            ).Envelopes
        );
        await deadLetters.ReplayAsync(
            new DeadLetterEnvelopeQuery([deadLetter.Id]),
            TestContext.Current.CancellationToken
        );
        await BrokerEnvironment.WaitUntilAsync(
            environment.Inventory,
            $"exists (select from inventory.pouryakit_receipts where subscription = '{Audit}')",
            "The replayed broker envelope never reached the audit."
        );
        await BrokerEnvironment.WaitUntilSettledAsync(environment.Inventory);

        Assert.Equal(
            [
                [$"local://{Audit}/", "Handled"],
                [HostQueue, "Handled"],
            ],
            await Sql.RowsAsync(
                environment.Inventory,
                $"select received_at, status from wolverine.wolverine_incoming_envelopes where received_at in ('{HostQueue}', 'local://{Audit}/') and message_type = 'catalog.product-created.v1' order by 1"
            )
        );
        Assert.Equal(
            [
                [product],
            ],
            await AuditsWithTheirReceiptAsync(environment)
        );
        Assert.Single(DeliveryProbes.AttemptsOf(Audit, created));
        Assert.Equal(
            (1L, 1L),
            (
                await ReceiptsAsync(environment, StockItems),
                await StockItemsAsync(environment, product)
            )
        );
    }

    [Fact]
    public async Task A_retry_waiting_in_the_other_host_survives_its_kill_and_a_publication_made_while_it_is_down_arrives_after_it_starts()
    {
        var environment = await broker.CreateEnvironmentAsync();
        Action<WebApplicationBuilder> slowRetry = builder =>
            builder.Services.Configure<PouryaKitMessagingOptions>(messaging =>
                messaging.RetryDelays = [TimeSpan.FromSeconds(4)]
            );
        var inventory = await environment.StartInventoryHostAsync(configure: slowRetry);
        await using var catalog = await environment.StartCatalogHostAsync();
        Guid first = Guid.CreateVersion7(),
            second = Guid.CreateVersion7();

        using (DeliveryProbes.FailOnceAfterReceipt(Audit))
        {
            await BrokerEnvironment.CreateProductAsync(catalog, first, "Lamp");
            await BrokerEnvironment.WaitUntilAsync(
                environment.Inventory,
                $"exists (select from wolverine.wolverine_incoming_envelopes where received_at = 'local://{Audit}/' and status = 'Scheduled' and attempts = 1)",
                "The audit's retry was never stored."
            );
        }

        await BrokerEnvironment.KillAsync(inventory);
        await BrokerEnvironment.CreateProductAsync(catalog, second, "Desk lamp");
        // Its ProductCreatedV1, V2 and Snapshot wait in the Host's queue.
        await WaitAsync(
            async () => await environment.BrokerMessagesAsync(InventoryHost.ServiceName) == 3,
            "The publication made while the Host was down is not waiting in its queue."
        );

        using var restarted = await environment.StartInventoryHostAsync(configure: slowRetry);
        await BrokerEnvironment.WaitUntilAsync(
            environment.Inventory,
            $"(select count(*) from inventory.pouryakit_receipts where subscription in ('{Audit}', '{StockItems}')) = 4",
            "Not every delivery arrived after the restart."
        );
        await BrokerEnvironment.WaitUntilSettledAsync(environment.Inventory);

        Assert.Equal(
            (2, 1),
            (
                DeliveryProbes
                    .AttemptsOf(Audit, await environment.CreatedEventOfAsync(first))
                    .Count,
                DeliveryProbes
                    .AttemptsOf(Audit, await environment.CreatedEventOfAsync(second))
                    .Count
            )
        );
        Assert.Equal(
            (1L, 1L),
            (await StockItemsAsync(environment, first), await StockItemsAsync(environment, second))
        );
    }

    [Fact]
    public async Task A_host_whose_broker_connections_drop_while_it_runs_consumes_its_queue_again_and_a_publication_committed_after_arrives()
    {
        var environment = await broker.CreateEnvironmentAsync();
        using var inventory = await environment.StartInventoryHostAsync();
        await using var catalog = await environment.StartCatalogHostAsync();
        var product = Guid.CreateVersion7();

        await broker.DropConnectionsAsync(environment.Broker);
        await WaitAsync(
            async () => await environment.BrokerConsumersAsync(InventoryHost.ServiceName) == 0,
            "The drop never reached the Host's listener."
        );
        await WaitAsync(
            async () => await environment.BrokerConsumersAsync(InventoryHost.ServiceName) > 0,
            "The Host never consumed its queue again after the drop."
        );

        await BrokerEnvironment.CreateProductAsync(catalog, product, "Lamp");
        await BrokerEnvironment.WaitUntilAsync(
            environment.Inventory,
            $"exists (select from inventory.stock_items where product_id = '{product}')",
            "The publication committed after the drop never arrived."
        );
        await BrokerEnvironment.WaitUntilSettledAsync(environment.Inventory);
        await BrokerEnvironment.WaitUntilSettledAsync(environment.Catalog);

        Assert.Equal(
            (1L, 1L),
            (
                await ReceiptsAsync(environment, StockItems),
                await StockItemsAsync(environment, product)
            )
        );
    }

    #region two-service-names-receive-every-message
    [Fact]
    public async Task Two_hosts_with_different_service_names_each_receive_every_message()
    {
        var environment = await broker.CreateEnvironmentAsync();
        var warehouseDatabase = await environment.CreateDatabaseAsync("warehouse");
        using var inventory = await environment.StartInventoryHostAsync();
        using var warehouse = await environment.StartInventoryHostAsync(
            "warehouse",
            warehouseDatabase
        );
        await using var catalog = await environment.StartCatalogHostAsync();
        Guid[] products = [Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()];

        foreach (var product in products)
        {
            await BrokerEnvironment.CreateProductAsync(catalog, product, $"Lamp {product}");
        }

        foreach (var database in new[] { environment.Inventory, warehouseDatabase })
        {
            await BrokerEnvironment.WaitUntilAsync(
                database,
                "(select count(*) from inventory.stock_items) = 3",
                "A Host did not receive every product."
            );
            await BrokerEnvironment.WaitUntilSettledAsync(database);
            Assert.Equal(
                [.. products.Order().Select(product => new object[] { product })],
                await Sql.RowsAsync(
                    database,
                    "select product_id from inventory.stock_items order by 1"
                )
            );
        }
    }
    #endregion

    [Fact]
    public async Task Each_local_copy_carries_the_broker_envelopes_identity_and_operation_context_under_an_id_of_its_own()
    {
        var environment = await broker.CreateEnvironmentAsync();
        using var inventory = await environment.StartInventoryHostAsync();
        await using var catalog = await environment.StartCatalogHostAsync();
        var product = Guid.CreateVersion7();
        var context = new OperationContext(Actor.Person("ada"), "tenant-51");

        await BrokerEnvironment.CreateProductAsync(catalog, product, "Lamp", context);
        var created = await environment.CreatedEventOfAsync(product);
        await BrokerEnvironment.WaitUntilAsync(
            environment.Inventory,
            $"(select count(*) from inventory.pouryakit_receipts where subscription in ('{Audit}', '{StockItems}')) = 2",
            "Inventory.Host did not apply ProductCreatedV1 in both subscriptions."
        );

        // The store keeps envelope ids alone: the broker envelope and each subscription's copy have one of their own.
        Assert.Equal(
            MessageIdentity.IdOnly,
            inventory
                .Services.GetRequiredService<IWolverineRuntime>()
                .Options.Durability.MessageIdentity
        );
        Assert.Equal(
            3L,
            await BrokerEnvironment.ScalarAsync<long>(
                environment.Inventory,
                "select count(distinct id) from wolverine.wolverine_incoming_envelopes where message_type = 'catalog.product-created.v1'"
            )
        );
        // Both Receipts are of the Domain Event Catalog published it for.
        Assert.Equal(
            [
                [Audit, created],
                [StockItems, created],
            ],
            await Sql.RowsAsync(
                environment.Inventory,
                $"select subscription, message_id from inventory.pouryakit_receipts where subscription in ('{Audit}', '{StockItems}') order by 1"
            )
        );
        // The delivery continues, as a trusted continuation, the Operation of Catalog that published it.
        var publishing = Assert.Single(DeliveryProbes.ContextsOf(PublishProductCreated, created));
        var delivered = Assert.Single(DeliveryProbes.ContextsOf(StockItems, created));
        Assert.Equal(
            (context.Actor, context.Tenant, context.CorrelationId, publishing.OperationId, true),
            (
                delivered.Actor,
                delivered.Tenant,
                delivered.CorrelationId,
                delivered.CausationId,
                delivered.IsTrustedContinuation
            )
        );
    }

    [Fact]
    public async Task Each_version_crosses_the_broker_under_its_own_wire_name_and_reaches_only_its_own_subscriptions()
    {
        var environment = await broker.CreateEnvironmentAsync();
        using var inventory = await environment.StartInventoryHostAsync();
        await using var catalog = await environment.StartCatalogHostAsync();

        await BrokerEnvironment.CreateProductAsync(catalog, Guid.CreateVersion7(), "Lamp");
        await BrokerEnvironment.WaitUntilAsync(
            environment.Inventory,
            "(select count(*) from inventory.pouryakit_receipts) = 4",
            "Inventory.Host did not apply every message."
        );
        await BrokerEnvironment.WaitUntilSettledAsync(environment.Inventory);

        Assert.Equal(
            [
                [$"local://{Audit}/", "catalog.product-created.v1"],
                [$"local://{CatalogProducts}/", "catalog.product-snapshot.v1"],
                [$"local://{ProductCategories}/", "catalog.product-created.v2"],
                [$"local://{StockItems}/", "catalog.product-created.v1"],
                [HostQueue, "catalog.product-created.v1"],
                [HostQueue, "catalog.product-created.v2"],
                [HostQueue, "catalog.product-snapshot.v1"],
            ],
            await Sql.RowsAsync(
                environment.Inventory,
                "select received_at, message_type from wolverine.wolverine_incoming_envelopes where message_type like 'catalog.%' order by 1, 2"
            )
        );
        Assert.Equal(
            0L,
            await BrokerEnvironment.ScalarAsync<long>(
                environment.Inventory,
                "select count(*) from wolverine.wolverine_dead_letters"
            )
        );
    }

    [Fact]
    public async Task Snapshots_redelivered_across_the_broker_in_reverse_order_leave_the_newest()
    {
        var environment = await broker.CreateEnvironmentAsync();
        using var inventory = await environment.StartInventoryHostAsync();
        await using var catalog = await environment.StartCatalogHostAsync();
        var product = Guid.CreateVersion7();
        var envelopes = await ThreeVersionsAsync(environment, catalog, product, remove: false);
        await ForgetCopiesAsync(environment);

        foreach (var envelope in envelopes.Reverse())
        {
            await RedeliverAsync(environment, envelope);
            await BrokerEnvironment.WaitUntilSettledAsync(environment.Inventory);
        }

        Assert.Equal(
            [
                [product, "Floor lamp", 3L],
            ],
            await Sql.RowsAsync(
                environment.Inventory,
                "select product_id, name, version from inventory.catalog_products"
            )
        );
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Two_snapshot_versions_redelivered_across_the_broker_concurrently_leave_the_newer(
        bool newerFirst
    )
    {
        var environment = await broker.CreateEnvironmentAsync();
        using var inventory = await environment.StartInventoryHostAsync();
        await using var catalog = await environment.StartCatalogHostAsync();
        var product = Guid.CreateVersion7();
        var envelopes = await ThreeVersionsAsync(environment, catalog, product, remove: false);
        await ForgetCopiesAsync(environment);
        Guid first = newerFirst ? envelopes[2] : envelopes[1],
            second = newerFirst ? envelopes[1] : envelopes[2];

        // The first holds its newer-than-anything version uncommitted; the second arrives and must decide against it.
        using (var hold = DeliveryProbes.HoldAfterVersion(CatalogProducts))
        {
            await RedeliverAsync(environment, first);
            await hold.Held;
            await RedeliverAsync(environment, second);
            await BrokerEnvironment.WaitUntilAsync(
                environment.Inventory,
                "exists (select from pg_stat_activity where wait_event_type = 'Lock' and query like '%pouryakit_snapshot_versions%')",
                "The second delivery never waited on the held delivery's version."
            );
        }

        await BrokerEnvironment.WaitUntilSettledAsync(environment.Inventory);
        Assert.Equal(
            [
                [product, "Floor lamp", 3L],
            ],
            await Sql.RowsAsync(
                environment.Inventory,
                "select product_id, name, version from inventory.catalog_products"
            )
        );
    }

    [Fact]
    public async Task A_live_snapshot_redelivered_across_the_broker_after_the_tombstone_does_not_bring_the_copy_back()
    {
        var environment = await broker.CreateEnvironmentAsync();
        using var inventory = await environment.StartInventoryHostAsync();
        await using var catalog = await environment.StartCatalogHostAsync();
        var envelopes = await ThreeVersionsAsync(
            environment,
            catalog,
            Guid.CreateVersion7(),
            remove: true
        );
        await ForgetCopiesAsync(environment);

        foreach (var envelope in new[] { envelopes[2], envelopes[1] })
        {
            await RedeliverAsync(environment, envelope);
            await BrokerEnvironment.WaitUntilSettledAsync(environment.Inventory);
        }

        Assert.Empty(
            await Sql.RowsAsync(environment.Inventory, "select * from inventory.catalog_products")
        );
    }

    [Fact]
    public async Task A_message_the_host_cannot_fan_out_is_dead_lettered_in_its_store_at_its_broker_queue()
    {
        var environment = await broker.CreateEnvironmentAsync();
        using var inventory = await environment.StartInventoryHostAsync();
        var id = Guid.CreateVersion7();

        // A wire name the Host does not receive, such as one it received before a release removed its subscription.
        await using (
            var connection = await new ConnectionFactory
            {
                Uri = environment.Broker,
            }.CreateConnectionAsync(TestContext.Current.CancellationToken)
        )
        await using (
            var channel = await connection.CreateChannelAsync(
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
        {
            await channel.BasicPublishAsync(
                "",
                InventoryHost.ServiceName,
                mandatory: true,
                new BasicProperties
                {
                    MessageId = id.ToString(),
                    Type = "catalog.product-retired.v1",
                    ContentType = "application/json",
                },
                Encoding.UTF8.GetBytes("{}"),
                TestContext.Current.CancellationToken
            );
        }

        await BrokerEnvironment.WaitUntilAsync(
            environment.Inventory,
            "exists (select from wolverine.wolverine_dead_letters)",
            "The message was never dead-lettered."
        );
        Assert.Equal(
            [
                [id, HostQueue, "catalog.product-retired.v1"],
            ],
            await Sql.RowsAsync(
                environment.Inventory,
                "select id, received_at, message_type from wolverine.wolverine_dead_letters"
            )
        );
        Assert.Equal(0u, await environment.BrokerMessagesAsync(InventoryHost.ServiceName));
    }

    [Theory]
    [InlineData("in memory")]
    [InlineData("broker dead-letter queue")]
    public async Task A_host_whose_broker_queue_does_not_store_what_it_receives_or_sets_dead_letters_aside_on_the_broker_does_not_start(
        string change
    )
    {
        var environment = await broker.CreateEnvironmentAsync();
        using var inventory = environment.ComposeInventoryHost(configure: builder =>
            builder.Services.ConfigureWolverine(wolverine =>
            {
                var queue = wolverine.ListenToRabbitQueue(InventoryHost.ServiceName);
                _ =
                    change == "in memory"
                        ? queue.BufferedInMemory()
                        : queue.DeadLetterQueueing(
                            new DeadLetterQueue($"{InventoryHost.ServiceName}.dead-letters")
                        );
            })
        );

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HostFixture.StartHostAsync(inventory, TestContext.Current.CancellationToken)
        );

        Assert.Contains(
            change == "in memory"
                ? $"{HostQueue} (BufferedInMemory, dead letters Durable)"
                : $"{HostQueue} (Durable, dead letters Native)",
            thrown.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task A_product_catalog_creates_is_applied_by_inventory_host_running_as_its_own_process_with_the_operation_context()
    {
        var environment = await broker.CreateEnvironmentAsync();
        await environment.MigrateInventoryAsync();
        await using var catalog = await environment.StartCatalogHostAsync();
        var product = Guid.CreateVersion7();
        var context = new OperationContext(Actor.Person("ada"), "tenant-51");
        using var process = InventoryProcess.Start(environment);
        try
        {
            // Started once it consumes from its queue, which it declared and bound as it started.
            await WaitAsync(
                async () => await environment.BrokerConsumersAsync(InventoryHost.ServiceName) > 0,
                $"Inventory.Host never started:{Environment.NewLine}{process.Output}",
                seconds: 120
            );

            await BrokerEnvironment.CreateProductAsync(catalog, product, "Lamp", context);
            var created = await environment.CreatedEventOfAsync(product);
            await BrokerEnvironment.WaitUntilAsync(
                environment.Inventory,
                $"(select count(*) from inventory.pouryakit_receipts where message_id = '{created}') = 2",
                $"Inventory.Host did not apply the product:{Environment.NewLine}{process.Output}"
            );

            // Each effect commits with its subscription's Receipt, by the Actor who created the product.
            Assert.Equal(
                [
                    [product, "ada", StockItems],
                ],
                await Sql.RowsAsync(
                    environment.Inventory,
                    $"select s.product_id, s.created_by, r.subscription from inventory.stock_items s join inventory.pouryakit_receipts r on r.message_id = '{created}' and pg_xact_commit_timestamp(r.xmin) = pg_xact_commit_timestamp(s.xmin)"
                )
            );
            Assert.Equal(
                [
                    [product, "ada", ProductCategories],
                ],
                await Sql.RowsAsync(
                    environment.Inventory,
                    $"select c.product_id, c.filed_by, r.subscription from inventory.product_categories c join inventory.pouryakit_receipts r on r.message_id = '{created}' and pg_xact_commit_timestamp(r.xmin) = pg_xact_commit_timestamp(c.xmin)"
                )
            );
            // The copy the process delivered carried the publishing Operation's tenant, correlation and causation.
            var publishing = Assert.Single(
                DeliveryProbes.ContextsOf(PublishProductCreated, created)
            );
            var copy = EnvelopeSerializer.Deserialize(
                (byte[])
                    (
                        await Sql.RowsAsync(
                            environment.Inventory,
                            $"select body from wolverine.wolverine_incoming_envelopes where received_at = 'local://{StockItems}/'"
                        )
                    )[0][0]
            );
            Assert.Equal(
                ["tenant-51", context.CorrelationId.ToString(), publishing.OperationId.ToString()],
                [
                    copy.Headers[OperationContextHeaders.Tenant],
                    copy.Headers[OperationContextHeaders.CorrelationId],
                    copy.Headers[OperationContextHeaders.CausationId],
                ]
            );
        }
        finally
        {
            process.Kill();
        }
    }

    [Fact]
    public async Task A_typed_id_crosses_the_broker_as_its_bare_value_and_is_read_back_as_the_typed_id()
    {
        var environment = await broker.CreateEnvironmentAsync();
        using var inventory = await environment.StartInventoryHostAsync(configure: builder =>
        {
            builder.Services.Receive<ProbedV1>(ProbedV1.WireName);
            builder.Services.Subscribe<ProbedV1, ProbedIntegrationEventHandler>(
                "inventory.probed",
                typeof(InventoryModule)
            );
        });
        await using var catalog = await environment.StartCatalogHostAsync(builder =>
            builder.Services.Publish<ProbedV1>(ProbedV1.WireName, typeof(CatalogModule))
        );
        var sent = new ProbedV1(Guid.CreateVersion7(), new ProbeId(Guid.CreateVersion7()));

        // Straight through Wolverine, with the headers the Kit's Outbox writes: no Module of the sample publishes a typed ID.
        await using (var scope = catalog.Services.CreateAsyncScope())
        {
            var options = new DeliveryOptions().WithOperationContext(
                new OperationContext(GatewayTestExtensions.TestActor)
            );
            options.Headers[IntegrationEventHeaders.DomainEventId] = sent.Id.ToString();
            await scope
                .ServiceProvider.GetRequiredService<IMessageBus>()
                .PublishAsync(sent, options);
        }

        await BrokerEnvironment.WaitUntilAsync(
            environment.Inventory,
            $"exists (select from inventory.pouryakit_receipts where subscription = 'inventory.probed' and message_id = '{sent.Id}')",
            "Inventory.Host never applied the probe."
        );
        Assert.Equal(
            sent,
            Assert.Single(
                ProbedIntegrationEventHandler.Received,
                received => received.Id == sent.Id
            )
        );
        var body = Assert.Single(
            await Sql.RowsAsync(
                environment.Inventory,
                $"select body from wolverine.wolverine_incoming_envelopes where received_at = '{HostQueue}' and message_type = '{ProbedV1.WireName}'"
            )
        );
        var envelope = EnvelopeSerializer.Deserialize((byte[])body[0]);
        using var json = JsonDocument.Parse(envelope.Data!);
        Assert.Equal("application/json", envelope.ContentType);
        Assert.Equal(
            $"\"{sent.ProbeId.Value}\"",
            json.RootElement.GetProperty("probeId").GetRawText()
        );
    }

    // A Snapshot only Inventory.Host subscribes to leaves the Catalog Host as one broker envelope, which the store deletes
    // once sent (final review, finding 6): AssertNothingSentAsync fails on it once sent before the Operation returns, and
    // while its sender is paused, as Wolverine pauses it when the broker is unavailable, with the envelope still stored.
    [Fact]
    public async Task Assert_nothing_sent_fails_on_a_snapshot_sent_only_to_the_broker_once_sent_and_while_it_waits_for_the_broker()
    {
        var environment = await broker.CreateEnvironmentAsync();
        await using var fixture = new BrokerHostFixture(environment.Broker);
        await fixture.InitializeAsync();
        var catalog = fixture.Gateway<CatalogModule>();
        var product = Guid.CreateVersion7();
        await BrokerEnvironment.CreateProductAsync(fixture.Host, product, "Lamp");
        await fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);
        const string Sent =
            "The Operation sent catalog.product-snapshot.v1 to rabbitmq://exchange/catalog.product-snapshot.v1.";

        var sent = await Assert.ThrowsAsync<DeliveryAssertionException>(() =>
            fixture.AssertNothingSentAsync(
                async () =>
                {
                    var renamed = await catalog.SendAsync(
                        new RenameProductCommand(product, "Desk lamp"),
                        TestContext.Current.CancellationToken
                    );
                    // Sent, and deleted from the store, before the Operation returns.
                    await fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);
                    return renamed;
                },
                TestContext.Current.CancellationToken
            )
        );
        var sender = (SendingAgent)
            fixture
                .Host.Services.GetRequiredService<IWolverineRuntime>()
                .Endpoints.GetOrBuildSendingAgent(
                    new Uri("rabbitmq://exchange/catalog.product-snapshot.v1")
                );
        // Longer than the Operation and the read after it take on any machine; disposing the Host ends it.
        await sender.PauseAsync(TimeSpan.FromSeconds(60));
        var waiting = await Assert.ThrowsAsync<DeliveryAssertionException>(() =>
            fixture.AssertNothingSentAsync(
                () =>
                    catalog.SendAsync(
                        new RenameProductCommand(product, "Floor lamp"),
                        TestContext.Current.CancellationToken
                    ),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(Sent, sent.Message);
        Assert.Equal(Sent, waiting.Message);
    }

    // ModularMonolith.Host with the broker, as a HostFixture: Catalog, Billing and Ledger, with Inventory in another Host.
    private sealed class BrokerHostFixture(Uri broker) : HostFixture
    {
        protected override IHost CreateHost(string connectionString) =>
            AppFixture.ComposeHost(
                "Development",
                connectionString,
                new RecordedLogs(),
                null,
                $"--ConnectionStrings:broker={broker}"
            );
    }

    // Creates the product, renames it, then renames or removes it, each in an Operation of its own, and returns the
    // broker envelope that carried each version's Snapshot to Inventory.Host, oldest first.
    private static async Task<Guid[]> ThreeVersionsAsync(
        BrokerEnvironment environment,
        IHost catalog,
        Guid product,
        bool remove
    )
    {
        var gateway = catalog.Services.GetRequiredService<IGatewayProcessor<CatalogModule>>();
        await BrokerEnvironment.CreateProductAsync(catalog, product, "Lamp");
        await AppliedVersionAsync(environment, 1);
        Assert.True(
            (
                await gateway.SendAsync(
                    new RenameProductCommand(product, "Desk lamp"),
                    TestContext.Current.CancellationToken
                )
            ).IsSuccess
        );
        await AppliedVersionAsync(environment, 2);
        Assert.True(
            remove
                ? (
                    await gateway.SendAsync(
                        new RemoveProductsCommand(["Desk lamp"]),
                        TestContext.Current.CancellationToken
                    )
                ).IsSuccess
                : (
                    await gateway.SendAsync(
                        new RenameProductCommand(product, "Floor lamp"),
                        TestContext.Current.CancellationToken
                    )
                ).IsSuccess
        );
        await AppliedVersionAsync(environment, 3);
        await BrokerEnvironment.WaitUntilSettledAsync(environment.Inventory);
        return
        [
            .. (
                await Sql.RowsAsync(
                    environment.Inventory,
                    $"select id, body from wolverine.wolverine_incoming_envelopes where received_at = '{HostQueue}' and message_type = 'catalog.product-snapshot.v1'"
                )
            )
                .Select(row =>
                    (
                        Id: (Guid)row[0],
                        Version: long.Parse(
                            EnvelopeSerializer.Deserialize((byte[])row[1]).Headers[
                                SnapshotHeaders.Version
                            ]!,
                            CultureInfo.InvariantCulture
                        )
                    )
                )
                .OrderBy(envelope => envelope.Version)
                .Select(envelope => envelope.Id),
        ];
    }

    private static Task AppliedVersionAsync(BrokerEnvironment environment, long version) =>
        BrokerEnvironment.WaitUntilAsync(
            environment.Inventory,
            $"exists (select from inventory.pouryakit_snapshot_versions where subscription = '{CatalogProducts}' and version = {version})",
            $"Inventory.Host never applied the Snapshot's version {version}."
        );

    // What Inventory.Host has applied, as if none of the Snapshots had ever arrived.
    private static Task ForgetCopiesAsync(BrokerEnvironment environment) =>
        Sql.RowsAsync(
            environment.Inventory,
            $"delete from inventory.catalog_products; delete from inventory.pouryakit_snapshot_versions; delete from inventory.pouryakit_receipts where subscription = '{CatalogProducts}'"
        );

    // The store hands the broker envelope out again, as recovery does for one whose Host did not finish it, and it is
    // fanned out once more.
    private static async Task RedeliverAsync(BrokerEnvironment environment, Guid envelope) =>
        Assert.Single(
            await Sql.RowsAsync(
                environment.Inventory,
                $"update wolverine.wolverine_incoming_envelopes set status = 'Incoming', owner_id = 0 where id = '{envelope}' returning id"
            )
        );

    private static Task<long> ReceiptsAsync(BrokerEnvironment environment, string subscription) =>
        BrokerEnvironment.ScalarAsync<long>(
            environment.Inventory,
            $"select count(*) from inventory.pouryakit_receipts where subscription = '{subscription}'"
        );

    private static Task<long> AuditsAsync(BrokerEnvironment environment, Guid product) =>
        BrokerEnvironment.ScalarAsync<long>(
            environment.Inventory,
            $"select count(*) from tests.product_audits where product_id = '{product}'"
        );

    // Every audit committed together with the audit subscription's Receipt.
    private static Task<List<object[]>> AuditsWithTheirReceiptAsync(
        BrokerEnvironment environment
    ) =>
        Sql.RowsAsync(
            environment.Inventory,
            $"select a.product_id from tests.product_audits a join inventory.pouryakit_receipts r on r.subscription = '{Audit}' and pg_xact_commit_timestamp(r.xmin) = pg_xact_commit_timestamp(a.xmin)"
        );

    private static Task<long> StockItemsAsync(BrokerEnvironment environment, Guid product) =>
        BrokerEnvironment.ScalarAsync<long>(
            environment.Inventory,
            $"select count(*) from inventory.stock_items where product_id = '{product}'"
        );

    private static async Task ExecuteAsync(Npgsql.NpgsqlConnection connection, string sql)
    {
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task WaitAsync(
        Func<Task<bool>> condition,
        string because,
        int seconds = 30
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        while (!await condition())
        {
            Assert.False(timeout.IsCancellationRequested, because);
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}

internal sealed record ProbeId(Guid Value) : TypedId<Guid>(Value);

internal sealed record ProbedV1(Guid Id, ProbeId ProbeId)
{
    public const string WireName = "tests.probed.v1";
}

internal sealed class ProbedIntegrationEventHandler : IIntegrationEventHandler<ProbedV1>
{
    private static readonly ConcurrentQueue<ProbedV1> Applied = [];

    public static IReadOnlyList<ProbedV1> Received => [.. Applied];

    public Task<Result> Handle(ProbedV1 integrationEvent, CancellationToken cancellationToken)
    {
        Applied.Enqueue(integrationEvent);
        return Task.FromResult(Result.Success());
    }
}
