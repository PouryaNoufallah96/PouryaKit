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
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Realtime;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

// Ticket #133: a Notification Handler pushes through IRealtimePublisher, written as a PostgreSQL notification on its
// delivery's own transaction, and every instance's listener passes it to the clients of the delivery's tenant that
// listen on its Channel, through an SSE endpoint mapped with MapPouryaKitRealtime. Two instances of one Host share one
// store under Balanced durability, as the fixture's Host and one composed beside it do.
public sealed class RealtimeTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static readonly string NewProducts = $"/catalog/events?channel={NewProduct.Channel}";

    private static readonly string Notes = $"/stub/realtime?channel={StubChannels.Notes}";

    [Fact]
    public async Task A_product_committed_on_one_instance_reaches_a_client_connected_to_another()
    {
        await using var other = await StartAsync();
        using var client = AppFixture.ClientOf(other, "ada", AppFixture.TestTenant);
        await using var listening = await Listening.OpenAsync(client, NewProducts);

        // Created, delivered and pushed by the fixture's Host, whose local queue holds the Notification.
        var lamp = await CreateProductAsync(AppFixture.TestTenant, "Lamp");

        Assert.Equal((NewProduct.Channel, $$"""{"productId":"{{lamp}}","name":"Lamp"}"""), Pushed(await listening.NextAsync()));
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

        Assert.Equal($$"""{"productId":"{{lamp}}","name":"Lamp"}""", (await listeningX.NextAsync()).Data);
        Assert.Equal($$"""{"productId":"{{desk}}","name":"Desk"}""", (await listeningY.NextAsync()).Data);
    }

    [Fact]
    public async Task A_client_listens_only_on_the_channels_the_endpoint_admits_for_its_actor()
    {
        using var ada = Fixture.Client("ada");

        using var refused = await ada.GetAsync($"/stub/realtime?channel={StubChannels.InboxOf("grace")}", HttpCompletionOption.ResponseHeadersRead, Cancellation);
        using var unnamed = await ada.GetAsync("/stub/realtime", HttpCompletionOption.ResponseHeadersRead, Cancellation);
        await using var listening = await Listening.OpenAsync(ada, $"/stub/realtime?channel={StubChannels.InboxOf("ada")}");
        await PushAsync(new NotePushedDomainEvent(StubChannels.InboxOf("grace"), "for grace"));
        await PushAsync(new NotePushedDomainEvent(StubChannels.Notes, "for everyone"));
        await PushAsync(new NotePushedDomainEvent(StubChannels.InboxOf("ada"), "for ada"));

        Assert.Equal(RealtimeErrors.ChannelNotAdmitted.Code, Code(await ProblemResponses.ProblemAsync(refused, HttpStatusCode.Forbidden)));
        Assert.Equal(RealtimeErrors.ChannelRequired.Code, Code(await ProblemResponses.ProblemAsync(unnamed, HttpStatusCode.BadRequest)));
        Assert.Equal((StubChannels.InboxOf("ada"), """{"text":"for ada"}"""), Pushed(await listening.NextAsync()));
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
        var envelope = Encoding.UTF8.GetByteCount($$$"""{"channel":"{{{StubChannels.Notes}}}","tenant":"","body":{"text":""}}""");
        var refused = new NotePushedDomainEvent(StubChannels.Notes, new string('r', RealtimeNotificationLimit - envelope));
        var sent = new NotePushedDomainEvent(StubChannels.Notes, new string('s', RealtimeNotificationLimit - 1 - envelope));

        await PushAsync(refused, sent);

        Assert.Equal($$"""{"text":"{{sent.Text}}"}""", (await listening.NextAsync()).Data);
        Assert.Equal(RealtimeErrors.PayloadTooLarge, Assert.Single(PushNoteNotificationHandler.Pushed[refused.Id].Errors));
        Assert.True(PushNoteNotificationHandler.Pushed[sent.Id].IsSuccess);
    }

    [Fact]
    public async Task The_listener_listens_again_after_its_connection_is_killed_and_sends_every_client_a_gap()
    {
        using var client = Fixture.Client("ada");
        await using var listening = await Listening.OpenAsync(client, Notes);

        await Fixture.RowsAsync("select pg_terminate_backend(pid) from pg_stat_activity where application_name = 'pouryakit-realtime'");
        var gap = await listening.NextAsync();
        await PushAsync(new NotePushedDomainEvent(StubChannels.Notes, "after the gap"));

        Assert.Equal(RealtimeEvents.Gap, gap.EventType);
        Assert.Equal((StubChannels.Notes, """{"text":"after the gap"}"""), Pushed(await listening.NextAsync()));
    }

    [Fact]
    public async Task The_rate_limiter_counts_one_permit_for_a_connection_whatever_it_streams()
    {
        await using var host = await StartAsync("--PouryaKit:Web:RateLimit:Enabled=true", "--PouryaKit:Web:RateLimit:PermitLimit=2", "--PouryaKit:Web:RateLimit:Window=01:00:00");
        using var client = AppFixture.ClientOf(host, "ada");
        await using var listening = await Listening.OpenAsync(client, Notes);
        await PushAsync(new NotePushedDomainEvent(StubChannels.Notes, "one"), new NotePushedDomainEvent(StubChannels.Notes, "two"));
        await listening.NextAsync();
        await listening.NextAsync();

        using var second = await client.GetAsync("/stub/realtime", Cancellation);
        using var third = await client.GetAsync("/stub/realtime", Cancellation);

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.TooManyRequests), (second.StatusCode, third.StatusCode));
    }

    [Fact]
    public async Task Response_compression_leaves_the_event_stream_alone_while_it_compresses_json()
    {
        // Even where the Host asks to compress event streams: the stream states its own encoding, which compression keeps.
        await using var host = await StartAsync(
            builder => builder.Services.Configure<ResponseCompressionOptions>(compression => compression.MimeTypes = [.. ResponseCompressionDefaults.MimeTypes, "text/event-stream"]),
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
        using var connection = await client.GetAsync(Notes, HttpCompletionOption.ResponseHeadersRead, Cancellation);
        await using var body = await connection.Content.ReadAsStreamAsync(Cancellation);
        using var reader = new StreamReader(body);
        var connected = await reader.ReadLineAsync(Cancellation);

        // Stopping the Host ends every connection: the response ends as it was written.
        await host.StopAsync(Cancellation);
        var rest = await reader.ReadToEndAsync(Cancellation);

        await ProblemResponses.ProblemAsync(unanswered, HttpStatusCode.MethodNotAllowed);
        Assert.Equal((HttpStatusCode.OK, "text/event-stream"), (connection.StatusCode, connection.Content.Headers.ContentType?.MediaType));
        Assert.Equal($"event: {RealtimeEvents.Connected}", connected);
        Assert.Equal("data: \n\n", rest.ReplaceLineEndings("\n"));
    }

    // PostgreSQL's NOTIFY carries a payload shorter than this.
    private const int RealtimeNotificationLimit = 8000;

    private static (string Channel, string Data) Pushed(SseItem<string> item) => (item.EventType, item.Data);

    private static string? Code(JsonElement problem) => problem.GetProperty("errors")[0].GetProperty("code").GetString();

    // A second instance of the Host, on the fixture's store; disposing it stops it.
    private Task<WebApplication> StartAsync(params string[] arguments) => StartAsync(null, arguments);

    private async Task<WebApplication> StartAsync(Action<WebApplicationBuilder>? configure, params string[] arguments)
    {
        var host = AppFixture.ComposeHost("Development", Fixture.ConnectionString, Fixture.Logs, configure, arguments);
        await HostFixture.StartHostAsync(host, Cancellation);
        return host;
    }

    private async Task PushAsync(params DomainEvent[] raised)
    {
        var written = await Fixture
            .Gateway<StubModule>()
            .SendAsync(new WriteNoteRaisingCommand($"note {Guid.CreateVersion7()}", raised), Cancellation);
        Assert.True(written.IsSuccess);
    }

    // Waits for the push's delivery, so pushes of successive calls commit in their order.
    private async Task<Guid> CreateProductAsync(string tenant, string name)
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var context = new OperationContext(GatewayTestExtensions.TestActor, tenant);
        var category = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        Assert.True((await catalog.SendAsync(new CreateCategoryCommand(category, $"{name}s"), context, Cancellation)).IsSuccess);
        var (created, _) = await Fixture.WaitForDeliveriesAsync(
            () => catalog.SendAsync(new CreateProductCommand(product, category, name), context, Cancellation),
            Cancellation
        );
        Assert.True(created.IsSuccess);
        return product;
    }

    // One client's connection, read event by event once its connected event arrived.
    private sealed class Listening(HttpResponseMessage response, IAsyncEnumerator<SseItem<string>> events) : IAsyncDisposable
    {
        public HttpResponseMessage Response { get; } = response;

        public static async Task<Listening> OpenAsync(HttpClient client, string path)
        {
            var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, Cancellation);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var events = SseParser.Create(await response.Content.ReadAsStreamAsync(Cancellation)).EnumerateAsync(Cancellation).GetAsyncEnumerator(Cancellation);
            Listening listening = new(response, events);
            Assert.Equal(RealtimeEvents.Connected, (await listening.NextAsync()).EventType);
            return listening;
        }

        public async Task<SseItem<string>> NextAsync()
        {
            Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(Patience, Cancellation), "The connection ended.");
            return events.Current;
        }

        public async ValueTask DisposeAsync()
        {
            Response.Dispose();
            await events.DisposeAsync();
        }
    }
}
