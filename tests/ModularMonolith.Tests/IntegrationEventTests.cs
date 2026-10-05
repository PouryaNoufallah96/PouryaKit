using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Contracts.Products.Events.V1;
using Catalog.Products.Events.DomainEvents;
using Catalog.Products.Features.CreateProduct;
using Inventory;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

public sealed class IntegrationEventTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private const string InventorySubscription = "inventory.product-created";
    private const string PublishingSubscription = "catalog.product-created.publish-product-created";

    private DeliveryLog Notified => Fixture.Host.Services.GetRequiredService<DeliveryLog>();

    private IntegrationEventLog Received => Fixture.Host.Services.GetRequiredService<IntegrationEventLog>();

    [Fact]
    public async Task A_created_product_opens_one_stock_item_through_an_integration_event_with_the_domain_events_identity_and_time()
    {
        var product = Guid.CreateVersion7();
        List<object> written = [];
        IReadOnlyList<Delivery> deliveries;
        using (DeliveryProbes.RecordWritten(written))
        {
            (_, deliveries) = await Fixture.WaitForDeliveriesAsync(
                () => CreateProductAsync(product, new OperationContext(Actor.Person("ada"))),
                TestContext.Current.CancellationToken
            );
        }

        var created = Assert.Single(written.OfType<ProductCreatedDomainEvent>());
        Assert.Contains(new Delivery(InventorySubscription, "catalog.product-created.v1", false), deliveries);
        // Sender: the publishing Notification Handler's Receipt is keyed by the Domain Event's identity.
        Assert.Equal(
            [[created.Id]],
            await Fixture.RowsAsync($"select message_id from catalog.pouryakit_receipts where subscription = '{PublishingSubscription}'")
        );
        // Receiver: one stock item opened at the Domain Event's time, which PostgreSQL keeps to the microsecond, by the
        // Actor who created the product, and a Receipt keyed by the same identity.
        Assert.Equal(
            [[product, ToMicroseconds(created.OccurredOn), "ada"]],
            await Fixture.RowsAsync("select product_id, opened_on, created_by from inventory.stock_items")
        );
        Assert.Equal(
            [[InventorySubscription, created.Id]],
            await Fixture.RowsAsync("select subscription, message_id from inventory.pouryakit_receipts")
        );
    }

    [Fact]
    public async Task An_integration_event_is_stored_in_the_commit_of_the_publishing_notification_handlers_receipt()
    {
        List<object[]> receipt;
        List<object[]> envelope;
        // Inventory's delivery waits inside its transaction, so its envelope is not yet rewritten as handled.
        using (var hold = DeliveryProbes.HoldAfterReceipt(InventorySubscription))
        {
            await CreateProductAsync(Guid.CreateVersion7());
            await hold.Held;
            receipt = await Fixture.RowsAsync(
                $"select pg_xact_commit_timestamp(xmin) from catalog.pouryakit_receipts where subscription = '{PublishingSubscription}'"
            );
            envelope = await Fixture.RowsAsync(
                $"select status, pg_xact_commit_timestamp(xmin) from wolverine.wolverine_incoming_envelopes where received_at = 'local://{InventorySubscription}/'"
            );
        }

        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);
        var committedAt = Assert.Single(Assert.Single(receipt));
        Assert.IsType<DateTime>(committedAt);
        Assert.Equal([["Incoming", committedAt]], envelope);
    }

    [Fact]
    public async Task A_notification_handler_that_fails_after_publishing_leaves_no_integration_event()
    {
        var text = Unique();

        var (_, deliveries) = await Fixture.WaitForDeliveriesAsync(
            () => WriteNoteRaisingAsync(text, new NoteToShareDomainEvent(text, RefuseAfterSharing: true)),
            TestContext.Current.CancellationToken
        );

        // It published on every attempt; each attempt failed and stored nothing.
        Assert.Equal(1 + TestDeliveryServiceCollectionExtensions.RetryDelays.Count, Notified.For(text).Count);
        Assert.Equal(
            [new Delivery(MessageNames.SubscriptionNameOf(typeof(ShareNoteNotificationHandler), typeof(NoteToShareDomainEvent)), "modular-monolith.tests.note-to-share", true)],
            deliveries
        );
        Assert.DoesNotContain(Received.All, received => received.IntegrationEvent is NoteSharedV1 shared && shared.Text == text);
    }

    [Fact]
    public async Task Concurrent_deliveries_of_one_integration_event_apply_its_effect_once()
    {
        var product = Guid.CreateVersion7();
        using (var hold = DeliveryProbes.HoldAfterReceipt(InventorySubscription))
        {
            await CreateProductAsync(product);
            await hold.Held;
            var envelope = await Fixture.ScalarAsync<Guid>(
                $"select id from wolverine.wolverine_incoming_envelopes where received_at = 'local://{InventorySubscription}/'"
            );

            // While the first delivery runs, the store hands the same envelope out twice more, as InboxStaleTime does
            // for a delivery that outlasts it. Each runs at once and waits on the first one's uncommitted Receipt.
            for (var handedOut = 1; handedOut <= 2; handedOut++)
            {
                await Fixture.RowsAsync($"update wolverine.wolverine_incoming_envelopes set owner_id = 0 where id = '{envelope}'");
                await WaitForReceiptsWaitingAsync(handedOut);
            }
        }

        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1L, await Fixture.ScalarAsync<long>($"select count(*) from inventory.stock_items where product_id = '{product}'"));
        Assert.Equal(1L, await Fixture.ScalarAsync<long>("select count(*) from inventory.pouryakit_receipts"));
        Assert.Equal(
            [["Handled"]],
            await Fixture.RowsAsync($"select status from wolverine.wolverine_incoming_envelopes where received_at = 'local://{InventorySubscription}/'")
        );
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from wolverine.wolverine_dead_letters"));
    }

    [Fact]
    public async Task An_integration_event_handed_out_again_after_its_effect_committed_does_not_repeat_it()
    {
        var product = Guid.CreateVersion7();
        await Fixture.WaitForDeliveriesAsync(() => CreateProductAsync(product), TestContext.Current.CancellationToken);

        // The row as a crash between the receiver's commit and its acknowledgment leaves it, once recovery released it.
        Assert.Single(
            await Fixture.RowsAsync(
                $"update wolverine.wolverine_incoming_envelopes set status = 'Incoming', owner_id = 0 where received_at = 'local://{InventorySubscription}/' and status = 'Handled' returning id"
            )
        );
        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [["Handled"]],
            await Fixture.RowsAsync($"select status from wolverine.wolverine_incoming_envelopes where received_at = 'local://{InventorySubscription}/'")
        );
        Assert.Equal(1L, await Fixture.ScalarAsync<long>($"select count(*) from inventory.stock_items where product_id = '{product}'"));
    }

    [Fact]
    public async Task An_integration_event_handler_that_fails_is_retried_in_new_scopes_and_leaves_neither_its_effect_nor_its_receipt()
    {
        var text = Unique();

        var (_, deliveries) = await Fixture.WaitForDeliveriesAsync(
            () => WriteNoteRaisingAsync(text, new NoteCopyRequestedDomainEvent(text)),
            TestContext.Current.CancellationToken
        );

        var attempts = Received.All.Where(received => received.IntegrationEvent is NoteCopyRequestedV1 requested && requested.Text == text).ToList();
        Assert.Equal(1 + TestDeliveryServiceCollectionExtensions.RetryDelays.Count, attempts.Count);
        Assert.Equal(attempts.Count, attempts.Select(attempt => attempt.Operation).Distinct().Count());
        Assert.Contains(new Delivery("stub.note-copy-requested", "stub.note-copy-requested.v1", true), deliveries);
        Assert.Equal(
            [[typeof(IntegrationEventHandlerFailedException).FullName]],
            await Fixture.RowsAsync("select exception_type from wolverine.wolverine_dead_letters where received_at = 'local://stub.note-copy-requested/'")
        );
        Assert.Equal(
            0L,
            await Fixture.ScalarAsync<long>($"select count(*) from stub.notes where text = '{CopyFiledNoteNotificationHandler.CopyOf(text)}'")
        );
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.pouryakit_receipts where subscription = 'stub.note-copy-requested'"));
    }

    [Fact]
    public async Task An_integration_event_handler_runs_as_a_continuation_of_the_publishing_operation_with_the_domain_events_identity_and_time()
    {
        var text = Unique();
        var raising = new OperationContext(Actor.Person("ada"), tenant: "acme", correlationId: Guid.CreateVersion7());
        var shared = new NoteToShareDomainEvent(text);

        await Fixture.WaitForDeliveriesAsync(
            () => Fixture.Gateway<StubModule>().SendAsync(new WriteNoteRaisingCommand(text, [shared]), raising, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken
        );

        var notified = Assert.Single(Notified.For(text));
        var received = Assert.Single(Received.All, received => received.IntegrationEvent is NoteSharedV1 note && note.Text == text);
        Assert.Equal(new NoteSharedV1(shared.Id, shared.OccurredOn, text), received.IntegrationEvent);
        // No HTTP request anywhere: the Gateway call, the Notification's delivery, then the Integration Event's.
        Assert.Equal(
            (raising.Actor, raising.Tenant, raising.CorrelationId, (Guid?)notified.Context.OperationId),
            (received.Context.Actor, received.Context.Tenant, received.Context.CorrelationId, received.Context.CausationId)
        );
        Assert.Equal(typeof(StubModule), received.Operation.Module);
        Assert.Equal(
            [[shared.Id]],
            await Fixture.RowsAsync("select message_id from stub.pouryakit_receipts where subscription = 'stub.note-shared'")
        );
    }

    [Theory]
    [InlineData(nameof(Misuse.AnotherIdentity), "was published with the identity")]
    [InlineData(nameof(Misuse.UndeclaredType), "StubModule does not declare that it publishes ModularMonolith.Tests.Stub.UndeclaredV1")]
    [InlineData(nameof(Misuse.AnotherModulesType), "StubModule does not declare that it publishes Catalog.Contracts.Products.Events.V1.ProductCreatedV1")]
    public async Task A_notification_handler_that_publishes_what_it_must_not_fails_its_delivery(string misuse, string error)
    {
        var (_, deliveries) = await Fixture.WaitForDeliveriesAsync(
            () => WriteNoteRaisingAsync(Unique(), new PublishingMisusedDomainEvent(Enum.Parse<Misuse>(misuse))),
            TestContext.Current.CancellationToken
        );

        Assert.True(Assert.Single(deliveries).DeadLettered);
        Assert.Contains(error, await Fixture.ScalarAsync<string>("select exception_message from wolverine.wolverine_dead_letters"));
    }

    [Fact]
    public async Task Publishing_outside_a_notification_handlers_delivery_fails()
    {
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Fixture.Gateway<StubModule>().SendAsync(new PublishFromCommandCommand(Unique()), TestContext.Current.CancellationToken)
        );

        Assert.Contains("was published outside a Notification Handler's delivery", thrown.Message);
    }

    [Fact]
    public async Task A_stored_integration_event_whose_type_nothing_subscribes_to_any_more_is_dead_lettered_never_handled()
    {
        var product = Guid.CreateVersion7();
        await Fixture.WaitForDeliveriesAsync(() => CreateProductAsync(product), TestContext.Current.CancellationToken);

        // As if every subscription to the type had been removed while its delivery was stored.
        Assert.Single(
            await Fixture.RowsAsync(
                $"update wolverine.wolverine_incoming_envelopes set status = 'Incoming', owner_id = 0, message_type = 'catalog.product-retired.v1' where received_at = 'local://{InventorySubscription}/' returning id"
            )
        );
        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [[$"local://{InventorySubscription}/", "catalog.product-retired.v1"]],
            await Fixture.RowsAsync("select received_at, message_type from wolverine.wolverine_dead_letters")
        );
        Assert.Equal(
            0L,
            await Fixture.ScalarAsync<long>($"select count(*) from wolverine.wolverine_incoming_envelopes where message_type = 'catalog.product-retired.v1'")
        );
        Assert.Equal(1L, await Fixture.ScalarAsync<long>($"select count(*) from inventory.stock_items where product_id = '{product}'"));
    }

    [Fact]
    public async Task Startup_fails_on_a_subscription_to_an_integration_event_no_module_of_the_host_publishes()
    {
        await using var host = AppFixture.ComposeHost(
            "Development",
            Fixture.ConnectionString,
            Fixture.Logs,
            builder => builder.Services.Subscribe<TwinV1, TwinIntegrationEventHandler>("tests.unpublished-twin")
        );

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            $"{typeof(TwinIntegrationEventHandler).FullName} subscribes to {typeof(TwinV1).FullName} under tests.unpublished-twin, but no Module of this Host publishes it, so it has no wire name. Declare Publish<TwinV1>(wireName, module) in the publishing Module's Composition Root.",
            thrown.Message
        );
    }

    private async Task<Result<Guid>> CreateProductAsync(Guid product, OperationContext? context = null)
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var category = Guid.CreateVersion7();
        await catalog.SendAsync(new CreateCategoryCommand(category, $"Lamps {category}"), TestContext.Current.CancellationToken);
        return await catalog.SendAsync(
            new CreateProductCommand(product, category, $"Lamp {product}"),
            context ?? new OperationContext(GatewayTestExtensions.TestActor),
            TestContext.Current.CancellationToken
        );
    }

    private Task<Result> WriteNoteRaisingAsync(string text, DomainEvent raised) =>
        Fixture.Gateway<StubModule>().SendAsync(new WriteNoteRaisingCommand(text, [raised]), TestContext.Current.CancellationToken);

    private async Task WaitForReceiptsWaitingAsync(int count)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (
            await Fixture.ScalarAsync<long>(
                "select count(*) from pg_stat_activity where wait_event_type = 'Lock' and query like '%pouryakit_receipts%'"
            ) < count
        )
        {
            Assert.False(timeout.IsCancellationRequested, $"Fewer than {count} deliveries ever waited on the held delivery's Receipt.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    private static DateTime ToMicroseconds(DateTimeOffset time) => new(time.UtcTicks - time.UtcTicks % 10, DateTimeKind.Utc);

    private static string Unique() => $"note {Guid.CreateVersion7()}";
}

internal sealed record TwinV1(Guid Id);

internal sealed record OtherTwinV1(Guid Id);

internal sealed class TwinIntegrationEventHandler : IIntegrationEventHandler<TwinV1>
{
    public Task<Result> Handle(TwinV1 integrationEvent, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
}

internal sealed class OtherTwinIntegrationEventHandler : IIntegrationEventHandler<OtherTwinV1>
{
    public Task<Result> Handle(OtherTwinV1 integrationEvent, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
}

internal abstract class AbstractTwinIntegrationEventHandler : IIntegrationEventHandler<TwinV1>
{
    public abstract Task<Result> Handle(TwinV1 integrationEvent, CancellationToken cancellationToken);
}

internal sealed class NoteToShareDomainEventSubscriber : IIntegrationEventHandler<NoteToShareDomainEvent>
{
    public Task<Result> Handle(NoteToShareDomainEvent integrationEvent, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}
