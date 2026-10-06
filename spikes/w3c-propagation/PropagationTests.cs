using System.Diagnostics;
using System.Text;
using Catalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;

namespace W3cPropagation;

/// <summary>
/// The Proving Sample's real chain through the Kit's EF Outbox and durable local queues on PostgreSQL:
/// CreateProduct (Catalog) commits the Domain Event's Notification and the Product Snapshot; the Notification delivery
/// Operation in Catalog publishes the Integration Event ProductCreatedV1; Inventory's delivery Operation handles it.
/// A caller activity stands in for an incoming request whose W3C context is already current.
/// </summary>
public sealed class PropagationTests(PostgresFixture postgres)
{
    private const string Inventory = "inventory.product-created";
    private const string Publishing = "catalog.product-created.publish-product-created";
    private const string Snapshot = "inventory.catalog-products";
    private const string TraceState = "kit=spike-46,other=v1";

    [Theory]
    [InlineData(true, null)]
    [InlineData(true, TraceState)]
    [InlineData(false, null)]
    [InlineData(false, TraceState)]
    public async Task First_attempt(bool wolverineSourceListened, string? traceState)
    {
        TraceProbe.Clear();
        using var listener = TraceProbe.Listen(wolverineSourceListened);
        var connectionString = await postgres.NewDatabaseAsync();
        await using var host = await SpikeHost.StartAsync(connectionString, "A");

        var (caller, product) = await CreateProductAsync(host, traceState);
        await WaitUntilHandledAsync(connectionString, product);

        var report = await ReportAsync(connectionString, caller);
        Output(report.Text);
        var createProduct = Assert.Single(TraceProbe.Stopped, span => span.Tag("pouryakit.operation") == "CreateProductCommand");
        var publishing = ReceivingOperation(Publishing);
        var inventory = ReceivingOperation(Inventory);

        // Wolverine writes the activity current at publish, the Operation's, as the envelope's parent-id, in W3C form, in
        // every configuration; the Outbox row carries no tracestate, and the Kit's headers none either.
        Assert.Equal(createProduct.Id, report.Envelopes.Single(e => e.ReceivedAt == $"local://{Publishing}/").ParentId);
        Assert.Equal(createProduct.Id, report.Envelopes.Single(e => e.ReceivedAt == $"local://{Snapshot}/").ParentId);
        Assert.Equal(publishing.Id, report.Envelopes.Single(e => e.ReceivedAt == $"local://{Inventory}/").ParentId);
        Assert.Matches("^00-[0-9a-f]{32}-[0-9a-f]{16}-01$", publishing.Id);
        Assert.All(report.Envelopes, stored => Assert.False(stored.BodyHasTraceState));
        Assert.All(report.Envelopes, stored => Assert.DoesNotContain(stored.Headers.Keys, key => key.Contains("trace", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(traceState, createProduct.TraceState);

        if (wolverineSourceListened)
        {
            // Connected: the receiving Operation runs inside Wolverine's execution span, whose parent is the publishing
            // Operation. A parent, not a link; tracestate stops at the Outbox.
            var execution = Assert.Single(TraceProbe.Stopped, span => span.Source == TraceProbe.WolverineSourceName && span.SpanId == inventory.ParentSpanId);
            Assert.Equal("catalog.product-created.v1", execution.Name);
            Assert.Equal(publishing.SpanId, execution.ParentSpanId);
            Assert.Equal(caller.TraceId, inventory.TraceId);
            Assert.Equal(caller.TraceId, publishing.TraceId);
            Assert.Empty(execution.Links);
            Assert.Empty(inventory.Links);
            Assert.Null(inventory.TraceState);
            Assert.Null(publishing.TraceState);
        }
        else
        {
            // Not connected: with Wolverine's source not sampled no activity is current where the delivery runs, so the
            // receiving Operation starts a trace of its own, at every hop, although the envelope still names its parent.
            Assert.NotEqual(caller.TraceId, publishing.TraceId);
            Assert.NotEqual(caller.TraceId, inventory.TraceId);
            Assert.NotEqual(publishing.TraceId, inventory.TraceId);
            Assert.Equal(Span.NoParent, publishing.ParentSpanId);
            Assert.Equal(Span.NoParent, inventory.ParentSpanId);
        }

        // What restores it without Wolverine's span: the stored parent-id, and the tracestate the Kit would carry beside it.
        var parentId = report.Envelopes.Single(e => e.ReceivedAt == $"local://{Publishing}/").ParentId!;
        Assert.True(ActivityContext.TryParse(parentId, traceState, isRemote: true, out var parent));
        using (var restored = TraceProbe.Source.StartActivity(TraceProbe.OperationName, ActivityKind.Internal, parent))
        {
            Assert.NotNull(restored);
            Assert.Equal(caller.TraceId, restored.TraceId.ToHexString());
            Assert.Equal(createProduct.SpanId, restored.ParentSpanId.ToHexString());
            Assert.Equal(traceState, restored.TraceStateString);
        }
    }

    [Fact]
    public async Task Stored_retry()
    {
        TraceProbe.Clear();
        using var listener = TraceProbe.Listen(wolverine: true);
        var connectionString = await postgres.NewDatabaseAsync();
        await using var host = await SpikeHost.StartAsync(connectionString, "A");
        TraceProbe.FailFirst(Inventory);

        var (caller, product) = await CreateProductAsync(host, TraceState);
        await WaitUntilHandledAsync(connectionString, product);

        var report = await ReportAsync(connectionString, caller);
        Output(report.Text);
        var publishing = ReceivingOperation(Publishing);
        var attempts = TraceProbe.Receipts.Where(receipt => receipt.Subscription == Inventory).Select(receipt => receipt.Current!).ToList();
        Assert.Equal(2, attempts.Count);
        var executions = attempts
            .Select(attempt => Assert.Single(TraceProbe.Stopped, span => span.Source == TraceProbe.WolverineSourceName && span.SpanId == attempt.ParentSpanId))
            .ToList();

        // The retry, read back from the store, has the same parent as the first attempt. Wolverine records the failed
        // attempt for a link only in memory, and the store's reschedule does not rewrite the body, so no span is linked.
        Assert.All(attempts, attempt => Assert.Equal(caller.TraceId, attempt.TraceId));
        Assert.All(executions, execution => Assert.Equal(publishing.SpanId, execution.ParentSpanId));
        Assert.Equal("True", executions[1].Tag("wolverine.message.scheduled"));
        Assert.All(executions, execution => Assert.Empty(execution.Links));
        Assert.All(attempts, attempt => Assert.Empty(attempt.Links));
        Assert.All(attempts, attempt => Assert.Null(attempt.TraceState));
    }

    [Fact]
    public async Task Recovered_after_restart()
    {
        TraceProbe.Clear();
        using var listener = TraceProbe.Listen(wolverine: true);
        var connectionString = await postgres.NewDatabaseAsync();
        var (held, release) = TraceProbe.HoldFirst(Inventory);
        Span caller;
        Guid product;
        try
        {
            await using (var dying = await SpikeHost.StartAsync(connectionString, "A"))
            {
                (caller, product) = await CreateProductAsync(dying, TraceState);
                await held.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                // Wolverine's quick stop neither drains nor gives up what the Host holds, as a killed process leaves it.
                ((WolverineRuntime)dying.Services.GetRequiredService<IWolverineRuntime>()).StopMode = StopMode.Quick;
                await dying.StopAsync(TestContext.Current.CancellationToken);
            }

            await using var starting = await SpikeHost.StartAsync(connectionString, "B");
            await WaitUntilHandledAsync(connectionString, product);
            var report = await ReportAsync(connectionString, caller);
            Output(report.Text);
        }
        finally
        {
            release();
        }

        var publishing = ReceivingOperation(Publishing);
        var recovered = Assert.Single(TraceProbe.Receipts, receipt => receipt.Subscription == Inventory && receipt.Host == "B").Current!;
        var execution = Assert.Single(TraceProbe.Stopped, span => span.Source == TraceProbe.WolverineSourceName && span.SpanId == recovered.ParentSpanId);

        // Another Host recovers the stored envelope and still joins the publishing Operation's trace as a child.
        Assert.Equal(caller.TraceId, recovered.TraceId);
        Assert.Equal(publishing.SpanId, execution.ParentSpanId);
        Assert.Null(recovered.TraceState);
    }

    // The stand-in Operation activity a delivery to subscription ran in, from the Receipt it recorded.
    private static Span ReceivingOperation(string subscription) =>
        TraceProbe.Receipts.First(receipt => receipt.Subscription == subscription).Current
        ?? throw new InvalidOperationException($"No activity was current in the delivery to {subscription}.");

    private static async Task<(Span Caller, Guid Product)> CreateProductAsync(WebApplication host, string? traceState)
    {
        var catalog = host.Services.GetRequiredService<IGatewayProcessor<CatalogModule>>();
        var category = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        var context = new OperationContext(Actor.Person("ada"));
        // Before the caller's activity, so its Operation is not part of the observed trace.
        await catalog.SendAsync(Command("Catalog.Categories.Features.CreateCategory.CreateCategoryCommand", category, $"Lamps {category}"), context, TestContext.Current.CancellationToken);

        using var caller = TraceProbe.Source.StartActivity("caller", ActivityKind.Server)
            ?? throw new InvalidOperationException("The Spike's source is not listened to.");
        caller.TraceStateString = traceState;
        var created = await catalog.SendAsync(
            Command("Catalog.Products.Features.CreateProduct.CreateProductCommand", product, category, $"Lamp {product}"),
            new OperationContext(Actor.Person("ada")),
            TestContext.Current.CancellationToken
        );
        Assert.True(created.IsSuccess);
        return (Span.Of(caller), product);
    }

    // The sample's commands are internal to Catalog; the Spike builds them by name rather than change the sample.
    private static Command<Guid> Command(string typeName, params object[] arguments) =>
        (Command<Guid>)Activator.CreateInstance(typeof(CatalogModule).Assembly.GetType(typeName, throwOnError: true)!, arguments)!;

    private static async Task WaitUntilHandledAsync(string connectionString, Guid product)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (
            !await ScalarAsync<bool>(
                connectionString,
                $"""
                select (select count(*) from inventory.stock_items where product_id = '{product}') = 1
                   and exists (select 1 from wolverine.wolverine_incoming_envelopes where received_at = 'local://{Inventory}/')
                   and not exists (select 1 from wolverine.wolverine_incoming_envelopes where status <> 'Handled')
                """
            )
        )
        {
            Assert.False(timeout.IsCancellationRequested, "The Integration Event was not handled within 30 s.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    internal sealed record Stored(string ReceivedAt, string Status, string? ParentId, IReadOnlyDictionary<string, string?> Headers, bool BodyHasTraceState);

    internal sealed record Report(string Text, IReadOnlyList<Stored> Envelopes);

    private static async Task<Report> ReportAsync(string connectionString, Span caller)
    {
        var text = new StringBuilder();
        text.AppendLine($"Activity.DefaultIdFormat={Activity.DefaultIdFormat}");
        text.AppendLine($"caller: {caller}");
        text.AppendLine("Outbox writes (Activity.Current when the Unit of Work wrote the Outbox):");
        foreach (var write in TraceProbe.Writes)
        {
            text.AppendLine($"  host={write.Host} module={write.Module} messages=[{string.Join(",", write.Messages)}] current={write.Current?.ToString() ?? "<null>"}");
        }

        List<Stored> envelopes = [];
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand("select received_at, status, body from wolverine.wolverine_incoming_envelopes order by received_at", connection);
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                var body = reader.IsDBNull(2) ? null : (byte[])reader.GetValue(2);
                var envelope = body is null ? null : EnvelopeSerializer.Deserialize(body);
                envelopes.Add(
                    new Stored(
                        reader.GetString(0),
                        reader.GetString(1),
                        envelope?.ParentId,
                        envelope?.Headers ?? new Dictionary<string, string?>(),
                        body is not null && Encoding.UTF8.GetString(body).Contains("tracestate", StringComparison.OrdinalIgnoreCase)
                    )
                );
            }
        }

        text.AppendLine("Stored envelopes (wolverine_incoming_envelopes, body read with EnvelopeSerializer.Deserialize):");
        foreach (var stored in envelopes)
        {
            text.AppendLine(
                $"  {stored.ReceivedAt} {stored.Status} parent-id={stored.ParentId ?? "<null>"} bodyHasTracestate={stored.BodyHasTraceState} headers={{{string.Join(", ", stored.Headers.Select(header => $"{header.Key}={header.Value}"))}}}"
            );
        }

        text.AppendLine("Receipts (Activity.Current in the receiving scope, right before the subscribed handler):");
        foreach (var receipt in TraceProbe.Receipts)
        {
            text.AppendLine($"  host={receipt.Host} {receipt.Subscription} current={receipt.Current?.ToString() ?? "<null>"}");
        }

        text.AppendLine("Stopped activities of listened sources:");
        foreach (var span in TraceProbe.Stopped)
        {
            text.AppendLine($"  {span}");
        }

        return new Report(text.ToString(), envelopes);
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static void Output(string text) => TestContext.Current.TestOutputHelper?.WriteLine(text);
}
