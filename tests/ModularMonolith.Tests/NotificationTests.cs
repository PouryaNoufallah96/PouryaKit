using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Errors;
using Catalog.Products.Features.CreateProduct;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Misnamed;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Modularity;
using PouryaKit.Persistence.Postgres;
using PouryaKit.Testing;
using Wolverine;
using Wolverine.Runtime;

namespace ModularMonolith.Tests;

public sealed class NotificationTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private DeliveryLog Log => Fixture.Host.Services.GetRequiredService<DeliveryLog>();

    [Fact]
    public async Task A_new_product_is_announced_by_the_host_after_it_is_committed()
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var category = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        await catalog.SendAsync(new CreateCategoryCommand(category, "Lamps"), TestContext.Current.CancellationToken);

        var (created, deliveries) = await Fixture.WaitForDeliveriesAsync(
            () => catalog.SendAsync(new CreateProductCommand(product, category, "Lamp"), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken
        );

        Assert.True(created.IsSuccess);
        Assert.Equal(
            [
                new Delivery("billing.product-created", "catalog.product-created.v1", false),
                new Delivery("catalog.product-created.announce-new-product", "catalog.product-created", false),
                new Delivery("catalog.product-created.publish-product-created", "catalog.product-created", false),
                new Delivery("inventory.catalog-products", "catalog.product-snapshot.v1", false),
                new Delivery("inventory.product-categories", "catalog.product-created.v2", false),
                new Delivery("inventory.product-created", "catalog.product-created.v1", false),
            ],
            deliveries.OrderBy(delivery => delivery.Subscription, StringComparer.Ordinal)
        );
        Assert.Single(Fixture.Logs.Entries, entry => entry.Message == $"New in the catalog: Lamp ({product})");
    }

    [Fact]
    public async Task A_notification_is_stored_once_per_subscription_in_the_commit_of_the_change_and_sent_only_after_it()
    {
        var text = Unique();
        List<object[]> noteCommits = [];
        List<object[]> envelopes = [];
        var deliveredBeforeSending = -1;

        Result written;
        // Between the commit and the send: the change and its Notifications are visible, and nothing ran yet.
        using (
            CommitProbe.AfterCommit(async () =>
            {
                noteCommits = await Fixture.RowsAsync(
                    $"select pg_xact_commit_timestamp(xmin) from stub.notes where text = '{text}'"
                );
                envelopes = await Fixture.RowsAsync(
                    "select received_at, message_type, status, pg_xact_commit_timestamp(xmin) from wolverine.wolverine_incoming_envelopes order by received_at"
                );
                deliveredBeforeSending = Log.For(text).Count;
                return true;
            })
        )
        {
            written = await Fixture
                .Gateway<StubModule>()
                .SendAsync(
                    new WriteNoteRaisingCommand(text, [new NoteContestedDomainEvent(text)]),
                    TestContext.Current.CancellationToken
                );
        }

        Assert.True(written.IsSuccess);
        var committedAt = Assert.Single(Assert.Single(noteCommits));
        // Compared by commit timestamp, not by transaction id: EF Core saves inside a savepoint of the Unit of Work's
        // transaction, so the note and the envelopes carry different ids however they commit.
        Assert.Equal(
            [
                [$"local://{Subscription<RejectContestedNoteNotificationHandler, NoteContestedDomainEvent>()}/", "modular-monolith.tests.note-contested", "Incoming", committedAt],
                [$"local://{Subscription<AcceptContestedNoteNotificationHandler, NoteContestedDomainEvent>()}/", "modular-monolith.tests.note-contested", "Incoming", committedAt],
            ],
            envelopes.OrderByDescending(row => (string)row[0])
        );
        Assert.Equal(0, deliveredBeforeSending);
    }

    public static TheoryData<string> RolledBackOperations() => ["failed Result", "failed Domain Event Handler", "refused save"];

    [Theory]
    [MemberData(nameof(RolledBackOperations))]
    public async Task An_operation_that_rolls_back_stores_no_notification(string rollback)
    {
        var text = Unique();
        var stub = Fixture.Gateway<StubModule>();
        var catalog = Fixture.Gateway<CatalogModule>();
        var category = Guid.CreateVersion7();
        if (rollback == "refused save")
        {
            await catalog.SendAsync(new CreateCategoryCommand(category, "Lamps"), TestContext.Current.CancellationToken);
            // Its deliveries finish first, so none of them is taken for one of the refused save's.
            await Fixture.WaitForDeliveriesAsync(
                () => catalog.SendAsync(new CreateProductCommand(Guid.CreateVersion7(), category, text), TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken
            );
        }

        var failed = await Fixture.AssertNothingSentAsync<Result>(
            async () =>
                rollback switch
                {
                    "failed Result" => await stub.SendAsync(
                        new WriteNoteRaisingCommand(text, [new NotePostedDomainEvent(text)], Fail: true),
                        TestContext.Current.CancellationToken
                    ),
                    // The posted note's Domain Event is handled first, then a Domain Event Handler refuses the next.
                    "failed Domain Event Handler" => await stub.SendAsync(
                        new WriteNoteRaisingCommand(text, [new NotePostedDomainEvent(text), new NoteRefusedDomainEvent()]),
                        TestContext.Current.CancellationToken
                    ),
                    // The Notification is written before the save, which the database refuses for a name in use.
                    _ => await catalog.SendAsync(
                        new CreateProductCommand(Guid.CreateVersion7(), category, text),
                        TestContext.Current.CancellationToken
                    ),
                },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            rollback switch
            {
                "failed Result" => WriteNoteRaisingCommandHandler.Refused,
                "failed Domain Event Handler" => RefusalSecondReaction.Refused,
                _ => ProductErrors.NameInUse,
            },
            Assert.Single(failed.Errors)
        );
        Assert.Equal(0L, await Fixture.ScalarAsync<long>($"select count(*) from stub.notes where text = '{text}'"));
    }

    [Fact]
    public async Task A_notification_handler_runs_after_commit_in_a_scope_of_its_own_continuing_the_raising_operation()
    {
        var text = Unique();
        var raising = new OperationContext(Actor.Person("ada"), tenant: "acme", correlationId: Guid.CreateVersion7());

        await Fixture.WaitForDeliveriesAsync(
            () =>
                Fixture
                    .Gateway<StubModule>()
                    .SendAsync(
                        new WriteNoteRaisingCommand(text, [new NotePostedDomainEvent(text)]),
                        raising,
                        TestContext.Current.CancellationToken
                    ),
            TestContext.Current.CancellationToken
        );

        var delivered = Assert.Single(Log.For(text));
        Assert.True(delivered.NoteCommitted);
        Assert.Equal(typeof(StubModule), delivered.Operation.Module);
        Assert.Equal(
            (raising.Actor, raising.Tenant, raising.CorrelationId, (Guid?)raising.OperationId),
            (delivered.Context.Actor, delivered.Context.Tenant, delivered.Context.CorrelationId, delivered.Context.CausationId)
        );
        Assert.NotEqual(raising.OperationId, delivered.Context.OperationId);
    }

    [Fact]
    public async Task Two_notification_handlers_of_one_internal_domain_event_are_retried_and_recorded_independently()
    {
        var text = Unique();

        var (_, deliveries) = await Fixture.WaitForDeliveriesAsync(
            () =>
                Fixture
                    .Gateway<StubModule>()
                    .SendAsync(
                        new WriteNoteRaisingCommand(text, [new NoteContestedDomainEvent(text)]),
                        TestContext.Current.CancellationToken
                    ),
            TestContext.Current.CancellationToken
        );

        var runs = Log.For(text);
        var rejections = runs.Where(run => run.Handler == nameof(RejectContestedNoteNotificationHandler)).ToList();
        // The first attempt and one more after each retry delay, each in a scope of its own; the handler that
        // succeeded on the first attempt never ran again.
        Assert.Equal(1 + TestDeliveryServiceCollectionExtensions.RetryDelays.Count, rejections.Count);
        Assert.Equal(rejections.Count, rejections.Select(run => run.Operation).Distinct().Count());
        Assert.Single(runs, run => run.Handler == nameof(AcceptContestedNoteNotificationHandler));
        Assert.Equal(
            [
                (Subscription<AcceptContestedNoteNotificationHandler, NoteContestedDomainEvent>(), false),
                (Subscription<RejectContestedNoteNotificationHandler, NoteContestedDomainEvent>(), true),
            ],
            deliveries.Select(delivery => (delivery.Subscription, delivery.DeadLettered)).Order()
        );
    }

    [Fact]
    public async Task A_receipt_makes_a_redelivered_notification_apply_its_effect_once()
    {
        var text = Unique();
        var subscription = Subscription<CopyFiledNoteNotificationHandler, NoteFiledForCopyDomainEvent>();
        await Fixture.WaitForDeliveriesAsync(
            () =>
                Fixture
                    .Gateway<StubModule>()
                    .SendAsync(
                        new WriteNoteRaisingCommand(text, [new NoteFiledForCopyDomainEvent(text)]),
                        TestContext.Current.CancellationToken
                    ),
            TestContext.Current.CancellationToken
        );

        // The store hands the handled envelope out again, as it does after a crash between the handler's commit and
        // the acknowledgment, or when the inbox stale time passes during a long run.
        Assert.Single(
            await Fixture.RowsAsync(
                $"update wolverine.wolverine_incoming_envelopes set status = 'Incoming', owner_id = 0 where received_at = 'local://{subscription}/' and status = 'Handled' returning id"
            )
        );
        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            "Handled",
            await Fixture.ScalarAsync<string>(
                $"select status from wolverine.wolverine_incoming_envelopes where received_at = 'local://{subscription}/'"
            )
        );
        Assert.Equal(1L, await CopiesOfAsync(text));
        Assert.Equal(1L, await Fixture.ScalarAsync<long>("select count(*) from stub.pouryakit_receipts"));
    }

    [Fact]
    public async Task A_notification_handler_that_fails_leaves_neither_its_effect_nor_its_receipt()
    {
        var text = Unique();

        var (_, deliveries) = await Fixture.WaitForDeliveriesAsync(
            () =>
                Fixture
                    .Gateway<StubModule>()
                    .SendAsync(
                        new WriteNoteRaisingCommand(text, [new NoteFiledForBrokenCopyDomainEvent(text)]),
                        TestContext.Current.CancellationToken
                    ),
            TestContext.Current.CancellationToken
        );

        Assert.True(Assert.Single(deliveries).DeadLettered);
        Assert.Equal(0L, await CopiesOfAsync(text));
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.pouryakit_receipts"));
    }

    // Shows, in one process, that a Notification committed by a Host that dies before sending it stays with that Host
    // while others run, and is handled once after a Host starts. A killed process, and the recovery of a dead node's
    // deliveries in Balanced mode by stale-node detection, are proven only by Spike S1, as ADR 0004 records.
    [Fact]
    public async Task A_notification_committed_by_a_host_that_died_before_sending_it_is_handled_after_a_host_starts()
    {
        var text = Unique();
        await using var dying = AppFixture.ComposeHost("Development", Fixture.ConnectionString, Fixture.Logs);
        await using var starting = AppFixture.ComposeHost("Development", Fixture.ConnectionString, Fixture.Logs);
        await HostFixture.StartHostAsync(dying, TestContext.Current.CancellationToken);
        var stub = dying.Services.GetRequiredService<IGatewayProcessor<StubModule>>();
        List<object[]> afterDeath = [];

        var (written, deliveries) = await Fixture.WaitForDeliveriesAsync(
            async () =>
            {
                Result result;
                // The Host commits, then goes down before it sends what it committed.
                using (CommitProbe.AfterCommit(() => Task.FromResult(false)))
                {
                    result = await stub.SendAsync(
                        new WriteNoteRaisingCommand(text, [new NoteFiledForCopyDomainEvent(text)]),
                        TestContext.Current.CancellationToken
                    );
                }

                // Wolverine's quick stop neither drains nor gives up what the Host holds, as a killed process leaves it.
                ((WolverineRuntime)dying.Services.GetRequiredService<IWolverineRuntime>()).StopMode = StopMode.Quick;
                await dying.StopAsync(TestContext.Current.CancellationToken);
                // A bounded wait for something that must not happen: the fixture's Host, alive throughout, polls the
                // store several times and leaves the dead Host's delivery where it is.
                await Task.Delay(TestDeliveryServiceCollectionExtensions.PollingTime * 5, TestContext.Current.CancellationToken);
                // Beside the first occurrence of Inventory's recurring ReconcileStock, which the dying Host stored as it started.
                afterDeath = await Fixture.RowsAsync(
                    "select status, owner_id <> 0 from wolverine.wolverine_incoming_envelopes where received_at <> 'local://inventory.reconcile-stock/'"
                );

                await HostFixture.StartHostAsync(starting, TestContext.Current.CancellationToken);
                return result;
            },
            TestContext.Current.CancellationToken
        );

        Assert.True(written.IsSuccess);
        Assert.Equal([["Incoming", true]], afterDeath);
        Assert.False(Assert.Single(deliveries).DeadLettered);
        Assert.Equal(1L, await CopiesOfAsync(text));
        Assert.Equal(1L, await Fixture.ScalarAsync<long>("select count(*) from stub.pouryakit_receipts"));
    }

    [Fact]
    public async Task A_notification_committed_but_never_sent_is_delivered_on_a_live_host_after_the_inbox_stale_time()
    {
        var text = Unique();
        await using var live = AppFixture.ComposeHost(
            "Development",
            Fixture.ConnectionString,
            Fixture.Logs,
            builder =>
                builder.Services.Configure<PouryaKitMessageStoreOptions>(store => store.InboxStaleTime = TimeSpan.FromSeconds(1))
        );
        await HostFixture.StartHostAsync(live, TestContext.Current.CancellationToken);

        // The commit succeeds and the send never happens; the Host stays up, so no start or stop releases the delivery.
        var (written, deliveries) = await Fixture.WaitForDeliveriesAsync(
            async () =>
            {
                using (CommitProbe.AfterCommit(() => Task.FromResult(false)))
                {
                    return await live.Services.GetRequiredService<IGatewayProcessor<StubModule>>()
                        .SendAsync(
                            new WriteNoteRaisingCommand(text, [new NoteFiledForCopyDomainEvent(text)]),
                            TestContext.Current.CancellationToken
                        );
                }
            },
            TestContext.Current.CancellationToken
        );

        Assert.True(written.IsSuccess);
        Assert.False(Assert.Single(deliveries).DeadLettered);
        Assert.Equal(1L, await CopiesOfAsync(text));
    }

    [Fact]
    public async Task A_receipt_is_keyed_by_the_lower_cased_subscription_so_a_change_of_case_only_finds_it()
    {
        var text = Unique();
        var filed = new NoteFiledForCasedCopyDomainEvent(text);
        var queue = $"local://{CasedCopyNotificationHandler.Name.ToLowerInvariant()}/";
        await Fixture.WaitForDeliveriesAsync(
            () =>
                Fixture
                    .Gateway<StubModule>()
                    .SendAsync(new WriteNoteRaisingCommand(text, [filed]), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken
        );
        // Declared as stub.Cased-Copy; kept as its queue keeps it, whichever case a later declaration uses.
        var receipts = await Fixture.RowsAsync("select subscription, message_id from stub.pouryakit_receipts");

        Assert.Single(
            await Fixture.RowsAsync(
                $"update wolverine.wolverine_incoming_envelopes set status = 'Incoming', owner_id = 0 where received_at = '{queue}' returning id"
            )
        );
        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);

        Assert.Equal([["stub.cased-copy", filed.Id]], receipts);
        Assert.Equal(1L, await CopiesOfAsync(text));
    }

    [Fact]
    public void Startup_fails_on_a_missing_an_invalid_or_a_duplicate_name()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            builder.AddPouryaKitModules(modules => modules.Add<MisnamedModule>())
        );

        Assert.Equal(
            [
                "ModularMonolith.Tests.Misnamed.BadlyNamedDomainEvent's wire name misnamed.bad name is not a valid name: use letters and digits in segments joined by single dots or hyphens.",
                "ModularMonolith.Tests.Misnamed.BlankNotificationHandler's subscription name is missing; declare one or remove the empty name.",
                "ModularMonolith.Tests.Misnamed.ReservedNotificationHandler's subscription name agents is a queue Wolverine keeps for itself; choose another.",
                "ModularMonolith.Tests.Misnamed.SecondTwinNotificationHandler and ModularMonolith.Tests.Misnamed.FirstTwinNotificationHandler have the subscription name misnamed.twin, ignoring case; give one a SubscriptionName of its own.",
                "ModularMonolith.Tests.Misnamed.Shelved and ModularMonolith.Tests.Misnamed.ShelvedDomainEvent have the wire name modular-monolith.tests.misnamed.shelved, ignoring case; give one a WireName of its own.",
                "ModularMonolith.Tests.Misnamed.SpacedNotificationHandler's subscription name misnamed.has space is not a valid name: use letters and digits in segments joined by single dots or hyphens.",
                "ModularMonolith.Tests.Misnamed.UnnamedDomainEvent's wire name is missing; declare one or remove the empty name.",
            ],
            thrown.Message.Split(Environment.NewLine).Where(line => !line.Contains(MisshapenMark, StringComparison.Ordinal)).Order(StringComparer.Ordinal)
        );
    }

    [Fact]
    public void Startup_fails_on_a_notification_handler_that_is_generic_or_handles_several_an_abstract_or_a_generic_domain_event()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            builder.AddPouryaKitModules(modules => modules.Add<MisnamedModule>())
        );

        const string Rule = "a Notification Handler is a non-generic class that handles one concrete, non-generic Domain Event type.";
        Assert.Equal(
            [
                $"ModularMonolith.Tests.Misnamed.AnyEventNotificationHandler`1{MisshapenMark}TDomainEvent; {Rule}",
                $"ModularMonolith.Tests.Misnamed.AnyShapedNotificationHandler{MisshapenMark}ShapedDomainEvent; {Rule}",
                $"ModularMonolith.Tests.Misnamed.BoxedNotificationHandler{MisshapenMark}BoxedDomainEvent`1; {Rule}",
                $"ModularMonolith.Tests.Misnamed.TwoEventsNotificationHandler{MisshapenMark}PackedDomainEvent, SealedDomainEvent; {Rule}",
            ],
            thrown.Message.Split(Environment.NewLine).Where(line => line.Contains(MisshapenMark, StringComparison.Ordinal)).Order(StringComparer.Ordinal)
        );
    }

    [Fact]
    public void A_name_with_a_trailing_line_break_is_not_valid()
    {
        // A queue address cannot hold it, so a delivery could never find the subscription it was stored under.
        Assert.True(MessageNames.IsValid("stub.copy"));
        Assert.False(MessageNames.IsValid("stub.copy\n"));
    }

    [Fact]
    public async Task A_host_whose_subscription_queue_does_not_store_its_deliveries_fails_at_start()
    {
        var subscription = Subscription<CopyFiledNoteNotificationHandler, NoteFiledForCopyDomainEvent>();
        await using var host = AppFixture.ComposeHost(
            "Development",
            Fixture.ConnectionString,
            new RecordedLogs(),
            builder => builder.Services.ConfigureWolverine(wolverine => wolverine.LocalQueue(subscription).BufferedInMemory())
        );

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HostFixture.StartHostAsync(host, TestContext.Current.CancellationToken)
        );

        Assert.StartsWith(
            $"The queues of these subscriptions or delayed commands do not store what they receive, so their deliveries would not survive a restart: local://{subscription}/ (Application, BufferedInMemory).",
            thrown.Message
        );
    }

    [Fact]
    public async Task A_host_that_declares_notification_handlers_but_writes_no_outbox_fails_at_start()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.Services.AddPouryaKitNotificationHandlers(typeof(StubModule).Assembly);
        // The transport-neutral overload: Wolverine runs, but no Unit of Work writes an Outbox.
        builder.UsePouryaKitWolverine();
        using var host = builder.Build();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken)
        );
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.StartsWith("Notification Handlers, Integration Events, Snapshots or delayed commands are declared, but nothing writes them to an Outbox", thrown.Message);
    }

    [Fact]
    public async Task An_application_without_modules_delivers_its_notifications_with_their_receipts()
    {
        var text = Unique();
        var filed = new NoteFiledForCopyDomainEvent(text);
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            ["--environment", "Production", $"--ConnectionStrings:app={Fixture.ConnectionString}"]
        );
        builder.Services.UsePostgres<StubDbContext>(connectionName: "app", schema: "stub");
        builder.Services.AddPouryaKitCqrs(typeof(StubDbContext).Assembly);
        builder.Services.AddPouryaKitNotificationHandlers(typeof(StubDbContext).Assembly);
        builder.UsePouryaKitWolverine(connectionName: "app");
        builder.Services.UsePouryaKitTestDelivery();
        using var host = builder.Build();
        await HostFixture.StartHostAsync(host, TestContext.Current.CancellationToken);

        var (written, deliveries) = await Fixture.WaitForDeliveriesAsync(
            async () =>
            {
                await using var scope = host.Services.CreateAsyncScope();
                scope.ServiceProvider.BeginOperation(new OperationContext(Actor.System("single-module")));
                return await scope
                    .ServiceProvider.GetRequiredService<IDispatcher>()
                    .SendAsync(new WriteNoteRaisingCommand(text, [filed]), TestContext.Current.CancellationToken);
            },
            TestContext.Current.CancellationToken
        );
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(written.IsSuccess);
        Assert.False(Assert.Single(deliveries).DeadLettered);
        Assert.Equal(1L, await CopiesOfAsync(text));
        Assert.Equal(
            [[Subscription<CopyFiledNoteNotificationHandler, NoteFiledForCopyDomainEvent>(), filed.Id]],
            await Fixture.RowsAsync("select subscription, message_id from stub.pouryakit_receipts")
        );
    }

    [Fact]
    public async Task A_stored_delivery_to_a_subscription_the_host_no_longer_declares_is_dead_lettered_with_a_kit_error()
    {
        var text = Unique();
        await Fixture.WaitForDeliveriesAsync(
            () =>
                Fixture
                    .Gateway<StubModule>()
                    .SendAsync(
                        new WriteNoteRaisingCommand(text, [new NoteFiledForCopyDomainEvent(text)]),
                        TestContext.Current.CancellationToken
                    ),
            TestContext.Current.CancellationToken
        );

        // As if the subscription had been renamed while its delivery was stored.
        await Fixture.RowsAsync(
            "update wolverine.wolverine_incoming_envelopes set status = 'Incoming', owner_id = 0, received_at = 'local://stub.renamed/' returning id"
        );
        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [["local://stub.renamed/", typeof(UnknownSubscriptionException).FullName]],
            await Fixture.RowsAsync("select received_at, exception_type from wolverine.wolverine_dead_letters")
        );
        Assert.Equal(1L, await CopiesOfAsync(text));
    }

    [Fact]
    public async Task The_delivery_assertions_charge_an_earlier_operations_late_cascade_to_neither_operation_that_follows()
    {
        var stub = Fixture.Gateway<StubModule>();
        // Each asserted Operation sends nothing, but only once the earlier Operation's delivery has had time to cascade:
        // the waits build the race, they do not wait for a delivery.
        async Task<Result> NothingAfterTheCascade()
        {
            await Task.Delay(CascadeLateNotificationHandler.Delay * 3, TestContext.Current.CancellationToken);
            return await stub.SendAsync(new WriteNoteCommand(Unique()), TestContext.Current.CancellationToken);
        }

        var first = Unique();
        await stub.SendAsync(
            new WriteNoteRaisingCommand(first, [new NoteCascadingDomainEvent(first)]),
            TestContext.Current.CancellationToken
        );
        var nothing = await Fixture.AssertNothingSentAsync(NothingAfterTheCascade, TestContext.Current.CancellationToken);
        var second = Unique();
        await stub.SendAsync(
            new WriteNoteRaisingCommand(second, [new NoteCascadingDomainEvent(second)]),
            TestContext.Current.CancellationToken
        );
        var thrown = await Assert.ThrowsAsync<DeliveryAssertionException>(() =>
            Fixture.WaitForDeliveriesAsync(NothingAfterTheCascade, TestContext.Current.CancellationToken)
        );

        Assert.True(nothing.IsSuccess);
        Assert.StartsWith("The Operation sent nothing", thrown.Message);
        // Both cascades happened, and were waited for before each assertion began.
        Assert.Equal(1L, await CopiesOfAsync($"cascaded from {first}"));
        Assert.Equal(1L, await CopiesOfAsync($"cascaded from {second}"));
    }

    [Fact]
    public async Task Waiting_for_deliveries_fails_when_the_operation_sent_nothing()
    {
        // The note raises a Domain Event that no Notification Handler subscribes to, which stores nothing.
        var thrown = await Assert.ThrowsAsync<DeliveryAssertionException>(() =>
            Fixture.WaitForDeliveriesAsync(
                () =>
                    Fixture
                        .Gateway<StubModule>()
                        .SendAsync(
                            new WriteNoteRaisingCommand(Unique(), [new NoteFiledDomainEvent()]),
                            TestContext.Current.CancellationToken
                        ),
                TestContext.Current.CancellationToken
            )
        );

        Assert.StartsWith("The Operation sent nothing", thrown.Message);
    }

    [Fact]
    public async Task A_notification_handler_in_a_second_module_commits_its_effect_and_receipt_in_that_modules_own_data()
    {
        var text = Unique();
        var filed = new NoteFiledForCopyDomainEvent(text);

        await Fixture.WaitForDeliveriesAsync(
            () =>
                Fixture
                    .Gateway<StubModule>()
                    .SendAsync(new WriteNoteRaisingCommand(text, [filed]), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(1L, await CopiesOfAsync(text));
        Assert.Equal(
            [[Subscription<CopyFiledNoteNotificationHandler, NoteFiledForCopyDomainEvent>(), filed.Id]],
            await Fixture.RowsAsync("select subscription, message_id from stub.pouryakit_receipts")
        );
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from catalog.pouryakit_receipts"));
    }

    // What the registration says of a Notification Handler of the wrong shape, which the Misnamed assembly also holds.
    private const string MisshapenMark = " is a Notification Handler of ";

    private static string Unique() => $"note {Guid.CreateVersion7()}";

    private static string Subscription<THandler, TDomainEvent>() =>
        MessageNames.SubscriptionNameOf(typeof(THandler), typeof(TDomainEvent));

    private Task<long> CopiesOfAsync(string text) =>
        Fixture.ScalarAsync<long>($"select count(*) from stub.notes where text = '{CopyFiledNoteNotificationHandler.CopyOf(text)}'");
}

public sealed class MisnamedModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module) =>
        module.AddPouryaKitNotificationHandlers(typeof(MisnamedNotifications).Assembly, typeof(MisnamedModule));
}
