using System.Diagnostics;
using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Features.CreateProduct;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Modularity;
using PouryaKit.Observability;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

public sealed class ObservabilityTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private const string Publishing = "catalog.product-created.publish-product-created";
    private const string Inventory = "inventory.product-created";
    private const string TraceState = "kit=47,other=v1";

    // CreateProduct (Catalog) commits a Notification; its delivery Operation in Catalog publishes ProductCreatedV1 from
    // its own Unit of Work; Inventory's delivery Operation handles it. Through the Kit's durable local queues, with
    // Wolverine's own source listened or not.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_product_created_in_catalog_and_stocked_in_inventory_is_one_trace_with_the_upstream_tracestate(bool wolverineListened)
    {
        using var telemetry = new RecordedTelemetry(wolverineListened);
        var product = Guid.CreateVersion7();

        var upstream = await CreateProductAsync(product);

        var trace = telemetry.Of(upstream.TraceId);
        var createProduct = Assert.Single(trace, activity => Tag(activity, OperationTelemetry.OperationTag) == nameof(CreateProductCommand));
        var publishing = DeliveryOperation(Publishing, upstream.TraceId);
        var inventory = DeliveryOperation(Inventory, upstream.TraceId);
        // Each delivery's parent is Wolverine's span of its envelope when Wolverine's source is listened, whose parent is
        // the publishing Operation; otherwise the publishing Operation itself, which the Unit of Work's Outbox write
        // recorded as the envelope's parent-id while that Operation's activity was current.
        Assert.Equal(upstream.SpanId, createProduct.ParentSpanId);
        Assert.Equal(createProduct.SpanId, PublisherOf(publishing, trace, wolverineListened));
        Assert.Equal(publishing.SpanId, PublisherOf(inventory, trace, wolverineListened));
        List<Activity> chain = [createProduct, publishing, inventory];
        Assert.All(chain, activity => Assert.Equal(TraceState, activity.TraceStateString));
        Assert.Equal(
            (nameof(CatalogModule), "Person", OperationTelemetry.Succeeded),
            (Tag(createProduct, OperationTelemetry.ModuleTag), Tag(createProduct, OperationTelemetry.ActorKindTag), Tag(createProduct, OperationTelemetry.OutcomeTag))
        );
        Assert.Equal(("InventoryModule", OperationTelemetry.Succeeded), (Tag(inventory, OperationTelemetry.ModuleTag), Tag(inventory, OperationTelemetry.OutcomeTag)));
        Assert.Equal(1L, await Fixture.ScalarAsync<long>($"select count(*) from catalog.products where id = '{product}'"));
        Assert.Equal([[product, "ada"]], await Fixture.RowsAsync("select product_id, created_by from inventory.stock_items"));
    }

    // The first attempt runs from the envelope the Outbox published in memory; only the store's retry reads the parent-id
    // and the Kit's tracestate header back from the stored envelope.
    [Fact]
    public async Task A_stored_retry_of_a_delivery_stays_in_the_publishing_trace_with_its_tracestate()
    {
        using var telemetry = new RecordedTelemetry();
        var product = Guid.CreateVersion7();

        Activity upstream;
        using (DeliveryProbes.FailOnceAfterReceipt(Inventory))
        {
            upstream = await CreateProductAsync(product);
        }

        var publishing = DeliveryOperation(Publishing, upstream.TraceId);
        var attempts = DeliveryProbes.ActivitiesOf(Inventory).OfType<Activity>().Where(activity => activity.TraceId == upstream.TraceId).ToList();
        Assert.Equal([OperationTelemetry.Threw, OperationTelemetry.Succeeded], attempts.Select(attempt => Tag(attempt, OperationTelemetry.OutcomeTag)));
        Assert.All(attempts, attempt => Assert.Equal(publishing.SpanId, attempt.ParentSpanId));
        Assert.All(attempts, attempt => Assert.Equal(TraceState, attempt.TraceStateString));
        Assert.Equal([[product, "ada"]], await Fixture.RowsAsync("select product_id, created_by from inventory.stock_items"));
    }

    [Fact]
    public async Task Each_command_and_query_is_an_activity_and_a_fractional_duration_tagged_with_its_outcome()
    {
        using var telemetry = new RecordedTelemetry();
        var stub = Fixture.Gateway<StubModule>();
        var person = new OperationContext(Actor.Person("ada"));
        var cancellationToken = TestContext.Current.CancellationToken;
        Activity upstream;

        using (upstream = RecordedTelemetry.Upstream.StartActivity("request", ActivityKind.Server)!)
        {
            Assert.True((await stub.SendAsync(new WriteNoteCommand("written"), person, cancellationToken)).IsSuccess);
            Assert.True((await stub.QueryAsync(new FindNotesQuery(0, "written"), cancellationToken)).IsSuccess);
            Assert.True((await stub.SendAsync(new WriteNoteThenFailCommand("rejected", Ending.Fail), cancellationToken)).IsFailure);
            await Assert.ThrowsAsync<InvalidOperationException>(() => stub.SendAsync(new WriteNoteThenFailCommand("thrown", Ending.Throw), cancellationToken));
            Assert.True((await stub.SendAsync(new PostNoteThenNestCommand("nesting", NestedFailure.Handler), cancellationToken)).IsFailure);
        }

        var trace = telemetry.Of(upstream.TraceId);
        Assert.Equal(
            [
                (nameof(WriteNoteCommand), "Person", OperationTelemetry.Succeeded, ActivityStatusCode.Ok, null),
                (nameof(FindNotesQuery), "System", OperationTelemetry.Succeeded, ActivityStatusCode.Ok, null),
                (nameof(WriteNoteThenFailCommand), "System", OperationTelemetry.Failed, ActivityStatusCode.Error, WriteNoteThenFailCommandHandler.Rejected.Code),
                (nameof(WriteNoteThenFailCommand), "System", OperationTelemetry.Threw, ActivityStatusCode.Error, typeof(InvalidOperationException).FullName),
                (nameof(NestedNoteCommand), "System", OperationTelemetry.Failed, ActivityStatusCode.Error, NestedNoteCommandHandler.Refused.Code),
                (nameof(PostNoteThenNestCommand), "System", OperationTelemetry.Failed, ActivityStatusCode.Error, NestedNoteCommandHandler.Refused.Code),
            ],
            trace
                .Where(activity => activity.Source.Name == OperationTelemetry.SourceName)
                .Select(activity =>
                    (
                        Tag(activity, OperationTelemetry.OperationTag),
                        Tag(activity, OperationTelemetry.ActorKindTag),
                        Tag(activity, OperationTelemetry.OutcomeTag),
                        activity.Status,
                        activity.StatusDescription
                    )
                )
        );
        Assert.All(trace.Where(activity => activity.Source.Name == OperationTelemetry.SourceName), activity => Assert.Equal(nameof(StubModule), Tag(activity, OperationTelemetry.ModuleTag)));
        // A nested command is a child of the command that sent it; every outermost one, of the request.
        var nesting = Assert.Single(trace, activity => Tag(activity, OperationTelemetry.OperationTag) == nameof(PostNoteThenNestCommand));
        Assert.Equal(nesting.SpanId, Assert.Single(trace, activity => Tag(activity, OperationTelemetry.OperationTag) == nameof(NestedNoteCommand)).ParentSpanId);
        Assert.Equal(5, trace.Count(activity => activity.ParentSpanId == upstream.SpanId));
        Assert.Contains(trace, activity => activity.Events.Any(e => e.Name == "exception"));

        // The duration of each, in seconds with the fraction kept, with the activity's tags.
        var written = Assert.Single(telemetry.Durations, duration => (string?)duration.Tags[OperationTelemetry.OperationTag] == nameof(WriteNoteCommand));
        Assert.InRange(written.Seconds, double.Epsilon, 30);
        Assert.NotEqual(Math.Floor(written.Seconds), written.Seconds);
        Assert.Equal(
            new Dictionary<string, object?>
            {
                [OperationTelemetry.OperationTag] = nameof(WriteNoteCommand),
                [OperationTelemetry.ModuleTag] = nameof(StubModule),
                [OperationTelemetry.ActorKindTag] = "Person",
                [OperationTelemetry.OutcomeTag] = OperationTelemetry.Succeeded,
            },
            written.Tags
        );
        Assert.Contains(telemetry.Durations, duration => (string?)duration.Tags[OperationTelemetry.OutcomeTag] == OperationTelemetry.Threw);
    }

    // The category is created first, outside the trace; CreateProduct runs inside an upstream activity standing in for a
    // request whose W3C context ASP.NET Core restored, with a tracestate. Returns once every delivery it caused settled.
    private async Task<Activity> CreateProductAsync(Guid product)
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var category = Guid.CreateVersion7();
        await catalog.SendAsync(new CreateCategoryCommand(category, $"Lamps {category}"), TestContext.Current.CancellationToken);
        Activity? upstream = null;
        await Fixture.WaitForDeliveriesAsync(
            async () =>
            {
                using var request = upstream = RecordedTelemetry.Upstream.StartActivity("request", ActivityKind.Server)!;
                request.TraceStateString = TraceState;
                var created = await catalog.SendAsync(
                    new CreateProductCommand(product, category, $"Lamp {product}"),
                    new OperationContext(Actor.Person("ada")),
                    TestContext.Current.CancellationToken
                );
                Assert.True(created.IsSuccess);
                return created;
            },
            TestContext.Current.CancellationToken
        );
        return upstream!;
    }

    // The Operation activity of the one delivery to subscription in trace, as current when it recorded its Receipt.
    private static Activity DeliveryOperation(string subscription, ActivityTraceId trace) =>
        Assert.Single(DeliveryProbes.ActivitiesOf(subscription).OfType<Activity>(), activity => activity.TraceId == trace);

    // The span the delivery's envelope named as its parent: Wolverine's span's parent when that span is the delivery's.
    private static ActivitySpanId PublisherOf(Activity delivery, IReadOnlyList<Activity> trace, bool wolverineListened)
    {
        if (!wolverineListened)
        {
            return delivery.ParentSpanId;
        }

        var execution = Assert.Single(trace, activity => activity.SpanId == delivery.ParentSpanId);
        Assert.Equal("Wolverine", execution.Source.Name);
        return execution.ParentSpanId;
    }

    private static string? Tag(Activity activity, string name) => activity.GetTagItem(name) as string;
}
