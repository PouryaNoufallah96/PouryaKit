using System.Net;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Contracts.Products.Realtime;
using Catalog.Products.Features.CreateProduct;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModularMonolith.Tests.Stub;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;
using PouryaKit.Persistence.Postgres;
using PouryaKit.Realtime;
using PouryaKit.Testing;
using Wolverine;

namespace ModularMonolith.Tests;

// Ticket #133: a Notification Handler pushes through IRealtimePublisher, written as a PostgreSQL notification on its
// delivery's own transaction, and every instance's listener passes it to the clients of the delivery's tenant that
// listen on its Channel, through an SSE endpoint mapped with MapPouryaKitRealtime. Two instances of one Host share one
// store under Balanced durability, composed beside the fixture's Host with their durability set back from the test
// delivery's Solo.
public sealed class RealtimeTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static readonly string NewProducts = $"/catalog/events?channel={NewProduct.Channel}";

    private static readonly string Notes = $"/stub/realtime?channel={StubChannels.Notes}";

    [Fact]
    public async Task A_product_committed_on_one_instance_reaches_a_client_connected_to_another()
    {
        await using var first = await StartAsync(Balanced);
        await using var second = await StartAsync(Balanced);
        using var client = AppFixture.ClientOf(second, "ada", AppFixture.TestTenant);
        await using var listening = await Listening.OpenAsync(client, NewProducts);

        // Created, delivered and pushed by the first instance, whose local queue holds the Notification.
        var lamp = await CreateProductAsync(
            AppFixture.TestTenant,
            "Lamp",
            first.Services.GetRequiredService<IGatewayProcessor<CatalogModule>>()
        );

        Assert.Equal(
            (NewProduct.Channel, $$"""{"productId":"{{lamp}}","name":"Lamp"}"""),
            Pushed(await listening.NextAsync())
        );
    }

    [Fact]
    public async Task A_client_receives_only_the_events_of_its_own_tenant()
    {
        using var ofX = Fixture.Client("ada", "tenant-x");
        using var ofY = Fixture.Client("ada", "tenant-y");
        await using var listeningX = await Listening.OpenAsync(ofX, NewProducts);
        await using var listeningY = await Listening.OpenAsync(ofY, NewProducts);

        // Y's push commits before X's, so it would be X's first event had it reached X.
        var desk = await CreateProductAsync("tenant-y", "Desk");
        var lamp = await CreateProductAsync("tenant-x", "Lamp");

        Assert.Equal(
            $$"""{"productId":"{{lamp}}","name":"Lamp"}""",
            (await listeningX.NextAsync()).Data
        );
        Assert.Equal(
            $$"""{"productId":"{{desk}}","name":"Desk"}""",
            (await listeningY.NextAsync()).Data
        );
    }

    [Fact]
    public async Task A_client_listens_only_on_the_channels_the_endpoint_admits_for_its_actor()
    {
        using var ada = Fixture.Client("ada");

        using var refused = await ada.GetAsync(
            $"/stub/realtime?channel={StubChannels.InboxOf("grace")}",
            HttpCompletionOption.ResponseHeadersRead,
            Cancellation
        );
        using var unnamed = await ada.GetAsync(
            "/stub/realtime",
            HttpCompletionOption.ResponseHeadersRead,
            Cancellation
        );
        await using var listening = await Listening.OpenAsync(
            ada,
            $"/stub/realtime?channel={StubChannels.InboxOf("ada")}"
        );
        // Delivered before the admitted push, which would come second had either reached the client.
        await PushAsync(
            new NotePushedDomainEvent(StubChannels.InboxOf("grace"), "for grace"),
            new NotePushedDomainEvent(StubChannels.Notes, "for everyone")
        );
        await PushAsync(new NotePushedDomainEvent(StubChannels.InboxOf("ada"), "for ada"));

        Assert.Equal(
            RealtimeErrors.ChannelNotAdmitted.Code,
            Code(await ProblemResponses.ProblemAsync(refused, HttpStatusCode.Forbidden))
        );
        Assert.Equal(
            RealtimeErrors.ChannelRequired.Code,
            Code(await ProblemResponses.ProblemAsync(unnamed, HttpStatusCode.BadRequest))
        );
        Assert.Equal(
            (StubChannels.InboxOf("ada"), """{"text":"for ada"}"""),
            Pushed(await listening.NextAsync())
        );
    }

    // A trusted continuation in a Unit of Work, which a Notification Handler's delivery also is, is not one.
    [Fact]
    public async Task A_trusted_peers_command_and_a_command_continued_through_the_gateway_cannot_push()
    {
        var fromPeer = $"peer {Guid.CreateVersion7()}";
        var continued = $"continued {Guid.CreateVersion7()}";
        using var peer = Fixture.Client(AppFixture.TrustedPeer);
        peer.DefaultRequestHeaders.Add(OperationContextHeaders.ActorId, "ada");
        peer.DefaultRequestHeaders.Add(OperationContextHeaders.ActorKind, nameof(ActorKind.Person));
        peer.DefaultRequestHeaders.Add(
            OperationContextHeaders.CorrelationId,
            Guid.CreateVersion7().ToString()
        );
        peer.DefaultRequestHeaders.Add(
            OperationContextHeaders.CausationId,
            Guid.CreateVersion7().ToString()
        );

        using var sent = await peer.PostAsync(
            $"/stub/realtime/notes/{fromPeer}",
            null,
            Cancellation
        );
        var relayed = await Fixture
            .Gateway<StubModule>()
            .SendAsync(new RelayPushNoteCommand(continued), Cancellation);

        Assert.Equal(HttpStatusCode.NoContent, sent.StatusCode);
        Assert.True(relayed.IsSuccess);
        foreach (var text in new[] { fromPeer, continued })
        {
            var (trusted, refusal) = PushNoteCommandHandler.Pushes[text];
            Assert.True(trusted);
            Assert.Equal(
                $"{nameof(PushedNote)} was pushed outside a Notification Handler's delivery. Push a realtime event from a Notification Handler, so it is sent with that delivery's commit.",
                refusal
            );
        }
    }

    // The delivery's own context, passed to a Gateway call as it is, carries no authority into the scope that call opens:
    // the command it sends pushes and publishes nothing, so the delivery, which then rolls back, reaches nobody.
    [Fact]
    public async Task A_command_a_notification_handler_sends_through_the_gateway_with_its_own_context_cannot_push_or_publish()
    {
        var text = $"relayed {Guid.CreateVersion7()}";
        using var client = Fixture.Client("ada");
        await using var listening = await Listening.OpenAsync(client, Notes);

        await PushAsync(new NoteRelayedThenFailingDomainEvent(text));
        await PushAsync(new NotePushedDomainEvent(StubChannels.Notes, "after the relay"));

        Assert.Equal(
            (
                $"{nameof(PushedNote)} was pushed outside a Notification Handler's delivery. Push a realtime event from a Notification Handler, so it is sent with that delivery's commit.",
                $"{nameof(NoteSharedV1)} was published outside a Notification Handler's delivery. An Integration Event is published by a Notification Handler, so it is written to the Outbox with that delivery's commit."
            ),
            PushAndShareNoteCommandHandler.Refusals[text]
        );
        Assert.Equal("""{"text":"after the relay"}""", (await listening.NextAsync()).Data);
        Assert.DoesNotContain(
            Fixture.Host.Services.GetRequiredService<IntegrationEventLog>().All,
            received => received.IntegrationEvent is NoteSharedV1 shared && shared.Text == text
        );
    }

    // Ticket #159: the delivery step clears its record once the Notification Handler returns, before the Unit of Work
    // runs the Domain Event Handlers of what the handler changed in the same scope.
    [Fact]
    public async Task A_domain_event_handler_of_a_notification_handlers_change_cannot_push_or_publish_while_the_handler_can()
    {
        var text = $"handed on {Guid.CreateVersion7()}";
        using var client = Fixture.Client("ada");
        await using var listening = await Listening.OpenAsync(client, Notes);

        await PushAsync(new NoteHandedOnDomainEvent(text));
        await PushAsync(new NotePushedDomainEvent(StubChannels.Notes, "after the reaction"));

        Assert.Equal(
            (
                $"{nameof(PushedNote)} was pushed outside a Notification Handler's delivery. Push a realtime event from a Notification Handler, so it is sent with that delivery's commit.",
                $"{nameof(NoteSharedV1)} was published outside a Notification Handler's delivery. An Integration Event is published by a Notification Handler, so it is written to the Outbox with that delivery's commit."
            ),
            PushAndShareWrittenNoteReaction.Refusals[text]
        );
        Assert.Equal($$"""{"text":"{{text}}"}""", (await listening.NextAsync()).Data);
        Assert.Equal("""{"text":"after the reaction"}""", (await listening.NextAsync()).Data);
        Assert.DoesNotContain(
            Fixture.Host.Services.GetRequiredService<IntegrationEventLog>().All,
            received => received.IntegrationEvent is NoteSharedV1 shared && shared.Text == text
        );
    }

    // Ticket #159: a blank value names no Channel, whatever else the request names, and the endpoint's rule never sees it.
    [Theory]
    [InlineData("?channel=")]
    [InlineData("?channel=%20")]
    [InlineData("?channel=stub.notes&channel=")]
    public async Task A_request_naming_a_blank_channel_is_refused_as_naming_none(string query)
    {
        using var ada = Fixture.Client("ada");

        using var blank = await ada.GetAsync(
            $"/stub/realtime{query}",
            HttpCompletionOption.ResponseHeadersRead,
            Cancellation
        );

        Assert.Equal(
            RealtimeErrors.ChannelRequired.Code,
            Code(await ProblemResponses.ProblemAsync(blank, HttpStatusCode.BadRequest))
        );
    }

    [Fact]
    public async Task A_push_whose_delivery_rolls_back_reaches_no_client()
    {
        var text = $"note {Guid.CreateVersion7()}";
        using var client = Fixture.Client("ada");
        await using var listening = await Listening.OpenAsync(client, Notes);

        await PushAsync(new NotePushedThenFailingDomainEvent(text));

        // The first delivery pushed "1" and rolled back; its retry pushed "2" and committed.
        Assert.Equal($$"""{"text":"{{text}} 2"}""", (await listening.NextAsync()).Data);
    }

    [Fact]
    public async Task A_push_shorter_than_8000_bytes_is_sent_and_one_of_8000_is_refused_in_the_handler()
    {
        using var client = Fixture.Client("ada");
        await using var listening = await Listening.OpenAsync(client, Notes);
        // The notification is the Channel, the tenant, none here, and the event, in the Kit's JSON.
        var envelope = Encoding.UTF8.GetByteCount(
            $$$"""{"channel":"{{{StubChannels.Notes}}}","tenant":"","body":{"text":""}}"""
        );
        var refused = new NotePushedDomainEvent(
            StubChannels.Notes,
            new string('r', RealtimeNotificationLimit - envelope)
        );
        var sent = new NotePushedDomainEvent(
            StubChannels.Notes,
            new string('s', RealtimeNotificationLimit - 1 - envelope)
        );

        await PushAsync(refused, sent);

        Assert.Equal($$"""{"text":"{{sent.Text}}"}""", (await listening.NextAsync()).Data);
        Assert.Equal(
            RealtimeErrors.PayloadTooLarge,
            Assert.Single(PushNoteNotificationHandler.Pushed[refused.Id].Errors)
        );
        Assert.True(PushNoteNotificationHandler.Pushed[sent.Id].IsSuccess);
    }

    [Fact]
    public async Task The_listener_listens_again_after_its_connection_is_killed_and_sends_every_client_a_gap()
    {
        using var client = Fixture.Client("ada");
        await using var listening = await Listening.OpenAsync(client, Notes);

        await Fixture.RowsAsync(
            "select pg_terminate_backend(pid) from pg_stat_activity where application_name = 'pouryakit-realtime'"
        );
        var gap = await listening.NextAsync();
        await PushAsync(new NotePushedDomainEvent(StubChannels.Notes, "after the gap"));

        Assert.Equal(RealtimeEvents.Gap, gap.EventType);
        Assert.Equal(
            (StubChannels.Notes, """{"text":"after the gap"}"""),
            Pushed(await listening.NextAsync())
        );
    }

    [Fact]
    public async Task The_rate_limiter_counts_one_permit_for_a_connection_whatever_it_streams()
    {
        await using var host = await StartAsync(
            "--PouryaKit:Web:RateLimit:Enabled=true",
            "--PouryaKit:Web:RateLimit:PermitLimit=2",
            "--PouryaKit:Web:RateLimit:Window=01:00:00"
        );
        using var client = AppFixture.ClientOf(host, "ada");
        await using var listening = await Listening.OpenAsync(client, Notes);
        await PushAsync(
            new NotePushedDomainEvent(StubChannels.Notes, "one"),
            new NotePushedDomainEvent(StubChannels.Notes, "two")
        );
        await listening.NextAsync();
        await listening.NextAsync();

        using var second = await client.GetAsync("/stub/realtime", Cancellation);
        using var third = await client.GetAsync("/stub/realtime", Cancellation);

        Assert.Equal(
            (HttpStatusCode.BadRequest, HttpStatusCode.TooManyRequests),
            (second.StatusCode, third.StatusCode)
        );
    }

    [Fact]
    public async Task Response_compression_leaves_the_event_stream_alone_while_it_compresses_json()
    {
        // Even where the Host asks to compress event streams: the stream states its own encoding, which compression keeps.
        await using var host = await StartAsync(
            builder =>
                builder.Services.Configure<ResponseCompressionOptions>(compression =>
                    compression.MimeTypes = [
                        .. ResponseCompressionDefaults.MimeTypes,
                        "text/event-stream",
                    ]
                ),
            "--PouryaKit:Web:Compression:Enabled=true"
        );
        using var client = AppFixture.ClientOf(host, "ada");
        client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        using var json = await client.GetAsync("/stub/context", Cancellation);
        await using var listening = await Listening.OpenAsync(client, Notes);
        await PushAsync(new NotePushedDomainEvent(StubChannels.Notes, "uncompressed"));

        Assert.Equal(["gzip"], json.Content.Headers.ContentEncoding);
        Assert.Equal(["identity"], listening.Response.Content.Headers.ContentEncoding);
        Assert.Equal("""{"text":"uncompressed"}""", (await listening.NextAsync()).Data);
    }

    [Fact]
    public async Task Status_code_pages_leave_a_started_connection_alone_while_they_answer_an_empty_failure()
    {
        await using var host = await StartAsync();
        using var client = AppFixture.ClientOf(host, "ada");
        using var unanswered = await client.DeleteAsync("/stub/realtime", Cancellation);
        using var connection = await client.GetAsync(
            Notes,
            HttpCompletionOption.ResponseHeadersRead,
            Cancellation
        );
        await using var body = await connection.Content.ReadAsStreamAsync(Cancellation);
        using var reader = new StreamReader(body);
        var connected = await reader.ReadLineAsync(Cancellation);

        // Stopping the Host ends every connection: the response ends as it was written.
        await host.StopAsync(Cancellation);
        var rest = await reader.ReadToEndAsync(Cancellation);

        await ProblemResponses.ProblemAsync(unanswered, HttpStatusCode.MethodNotAllowed);
        Assert.Equal(
            (HttpStatusCode.OK, "text/event-stream"),
            (connection.StatusCode, connection.Content.Headers.ContentType?.MediaType)
        );
        Assert.Equal($"event: {RealtimeEvents.Connected}", connected);
        Assert.Equal("data: \n\n", rest.ReplaceLineEndings("\n"));
    }

    // EF Core applies a ConfigureDbContext after UsePouryaKitPostgres, so the DbContext connects as it says, whatever
    // name it was registered over.
    [Fact]
    public void A_dbcontext_that_configure_db_context_moves_off_the_listeners_connection_fails_the_host_when_built()
    {
        var builder = ComposeListener("Host=db;Database=app");
        builder.Services.ConfigureDbContext<StubDbContext>(options =>
            options.UseNpgsql("Host=db;Database=elsewhere")
        );

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(MovedOffTheListener, thrown.Message);
    }

    [Fact]
    public void A_dbcontext_moved_onto_a_data_source_on_another_database_fails_the_host_when_built()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=db;Database=elsewhere");
        var builder = ComposeListener("Host=db;Database=app");
        builder.Services.ConfigureDbContext<StubDbContext>(options =>
            options.UseNpgsql((string?)null).UseNpgsql(dataSource)
        );

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(MovedOffTheListener, thrown.Message);
    }

    // A data source gives its connection without the password, as does EF Core's own for a DbContext that maps an enum.
    [Fact]
    public void A_dbcontext_moved_onto_a_data_source_over_the_listeners_connection_string_builds()
    {
        const string connectionString = "Host=db;Database=app;Username=catalog;Password=secret";
        using var dataSource = NpgsqlDataSource.Create(connectionString);
        var builder = ComposeListener(connectionString);
        builder.Services.ConfigureDbContext<StubDbContext>(options =>
            options.UseNpgsql((string?)null).UseNpgsql(dataSource)
        );

        using var host = builder.Build();
    }

    // PostgreSQL's NOTIFY carries a payload shorter than this.
    private const int RealtimeNotificationLimit = 8000;

    private static (string Channel, string Data) Pushed(SseItem<string> item) =>
        (item.EventType, item.Data);

    private static string? Code(JsonElement problem) =>
        problem.GetProperty("errors")[0].GetProperty("code").GetString();

    private const string MovedOffTheListener =
        "EF Core connects StubDbContext, stored over the connection string app, with other settings than the connection string app that AddPouryaKitRealtime listens over, so what its Notification Handlers push may be written in another database than the listener's, and PostgreSQL hands a notification only to the listeners of its own database. Use one connection string for Realtime and every Module's DbContext.";

    // The Stub's DbContext and the listener on the connection string app, with the Kit's composition checks and no
    // message store, whose own check would report the same move. Nothing connects while the Host is built.
    private static HostApplicationBuilder ComposeListener(string connectionString)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder([
            "--environment",
            "Production",
            $"--ConnectionStrings:app={connectionString}",
        ]);
        builder.Services.UsePouryaKitPostgres<StubDbContext>(connectionName: "app", schema: "stub");
        builder.Services.AddPouryaKitRealtime(connectionName: "app");
        builder.AddPouryaKitCompositionChecks();
        return builder;
    }

    // A second instance of the Host, on the fixture's store; disposing it stops it.
    private Task<WebApplication> StartAsync(params string[] arguments) =>
        StartAsync(null, arguments);

    // Configured after the test delivery's Solo, so the composed instance runs as a Host does by default.
    private static void Balanced(WebApplicationBuilder builder) =>
        builder.Services.Configure<PouryaKitMessageStoreOptions>(store =>
            store.Durability = DurabilityMode.Balanced
        );

    private async Task<WebApplication> StartAsync(
        Action<WebApplicationBuilder>? configure,
        params string[] arguments
    )
    {
        var host = AppFixture.ComposeHost(
            "Development",
            Fixture.ConnectionString,
            Fixture.Logs,
            configure,
            arguments
        );
        await HostFixture.StartHostAsync(host, Cancellation);
        return host;
    }

    // Waits for every delivery the pushes cause, so pushes of successive calls commit in their order.
    private async Task PushAsync(params DomainEvent[] raised)
    {
        var (written, _) = await Fixture.WaitForDeliveriesAsync(
            () =>
                Fixture
                    .Gateway<StubModule>()
                    .SendAsync(
                        new WriteNoteRaisingCommand($"note {Guid.CreateVersion7()}", raised),
                        Cancellation
                    ),
            Cancellation
        );
        Assert.True(written.IsSuccess);
    }

    // Waits for the push's delivery, so pushes of successive calls commit in their order.
    private async Task<Guid> CreateProductAsync(
        string tenant,
        string name,
        IGatewayProcessor<CatalogModule>? through = null
    )
    {
        var catalog = through ?? Fixture.Gateway<CatalogModule>();
        var context = new OperationContext(GatewayTestExtensions.TestActor, tenant);
        var category = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        Assert.True(
            (
                await catalog.SendAsync(
                    new CreateCategoryCommand(category, $"{name}s"),
                    context,
                    Cancellation
                )
            ).IsSuccess
        );
        var (created, _) = await Fixture.WaitForDeliveriesAsync(
            () =>
                catalog.SendAsync(
                    new CreateProductCommand(product, category, name),
                    context,
                    Cancellation
                ),
            Cancellation
        );
        Assert.True(created.IsSuccess);
        return product;
    }

    // One client's connection, read event by event once its connected event arrived.
    private sealed class Listening(
        HttpResponseMessage response,
        IAsyncEnumerator<SseItem<string>> events
    ) : IAsyncDisposable
    {
        public HttpResponseMessage Response { get; } = response;

        public static async Task<Listening> OpenAsync(HttpClient client, string path)
        {
            var response = await client.GetAsync(
                path,
                HttpCompletionOption.ResponseHeadersRead,
                Cancellation
            );
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var events = SseParser
                .Create(await response.Content.ReadAsStreamAsync(Cancellation))
                .EnumerateAsync(Cancellation)
                .GetAsyncEnumerator(Cancellation);
            Listening listening = new(response, events);
            Assert.Equal(RealtimeEvents.Connected, (await listening.NextAsync()).EventType);
            return listening;
        }

        public async Task<SseItem<string>> NextAsync()
        {
            Assert.True(
                await events.MoveNextAsync().AsTask().WaitAsync(Patience, Cancellation),
                "The connection ended."
            );
            return events.Current;
        }

        public async ValueTask DisposeAsync()
        {
            Response.Dispose();
            await events.DisposeAsync();
        }
    }
}
