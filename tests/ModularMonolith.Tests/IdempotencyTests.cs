using System.Net;
using System.Net.Http.Json;
using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Features.ImportProducts;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence.EFCore;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

// Request idempotency against PostgreSQL: through a Gateway with a context that carries a request id, and over HTTP with
// the Idempotency-Key of an endpoint that requires one. CountNoteCommand writes a note and stores a Notification on
// every run, and answers a value no other run answers, so an unguarded repeat would succeed again visibly.
public sealed class IdempotencyTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private NoteRuns Runs => Fixture.Host.Services.GetRequiredService<NoteRuns>();

    [Fact]
    public async Task A_repeated_request_id_replays_the_first_success_without_running_writing_or_sending_anything()
    {
        var text = $"note {Guid.CreateVersion7()}";
        var stub = Fixture.Gateway<StubModule>();

        var (first, _) = await Fixture.WaitForDeliveriesAsync(
            () => stub.SendAsync(new CountNoteCommand(text), Context("r1"), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken
        );
        var repeat = await Fixture.AssertNothingSentAsync(
            () => stub.SendAsync(new CountNoteCommand(text), Context("r1"), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(1, first.Value.Number);
        Assert.Equal(first.Value, repeat.Value);
        Assert.Equal(1, Runs.Of(text));
        Assert.Equal(1L, await Fixture.ScalarAsync<long>($"select count(*) from stub.notes where text = '{text}'"));
        // Recorded in the Kit's one JSON representation: camelCase, a typed ID as its bare value.
        Assert.Equal(
            $$"""{"run":"{{first.Value.Run.Value}}","number":1,"text":"{{text}}"}""",
            await Fixture.ScalarAsync<string>("select response from stub.pouryakit_idempotency_keys")
        );
        // The Notification the first run stored was delivered as a continuation, which carries no request id.
        Assert.Null(Assert.Single(Fixture.Host.Services.GetRequiredService<DeliveryLog>().For(text)).Context.RequestId);
    }

    [Fact]
    public async Task A_failed_nested_command_leaves_no_key_so_a_later_attempt_runs_and_its_success_then_replays()
    {
        var text = $"note {Guid.CreateVersion7()}";
        var stub = Fixture.Gateway<StubModule>();

        var failed = await Fixture.AssertNothingSentAsync(
            () => stub.SendAsync(new PostNoteThenNestCommand(text, NestedFailure.Handler), Context("r1"), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken
        );
        var keysAfterFailure = await Fixture.ScalarAsync<long>("select count(*) from stub.pouryakit_idempotency_keys");
        var (retried, _) = await Fixture.WaitForDeliveriesAsync(
            () => stub.SendAsync(new PostNoteThenNestCommand(text, NestedFailure.None), Context("r1"), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken
        );
        // A command without a value replays its success too, and runs nothing: its nested note is not written again.
        var repeat = await Fixture.AssertNothingSentAsync(
            () => stub.SendAsync(new PostNoteThenNestCommand(text, NestedFailure.None), Context("r1"), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(NestedNoteCommandHandler.Refused, Assert.Single(failed.Errors));
        Assert.Equal(0L, keysAfterFailure);
        Assert.True(retried.IsSuccess);
        Assert.True(repeat.IsSuccess);
        Assert.Equal(
            2L,
            await Fixture.ScalarAsync<long>($"select count(*) from stub.notes where text in ('{text}', 'nested {text}')")
        );
    }

    [Fact]
    public async Task The_key_separates_actors_tenants_and_commands_and_a_null_tenant_keeps_one_stable_key()
    {
        var text = $"note {Guid.CreateVersion7()}";
        var stub = Fixture.Gateway<StubModule>();
        async Task<int> CountAs(OperationContext context) =>
            (await stub.SendAsync(new CountNoteCommand(text), context, TestContext.Current.CancellationToken)).Value.Number;

        Assert.Equal(1, await CountAs(Context("r1")));
        Assert.Equal(1, await CountAs(Context("r1")));
        Assert.Equal(2, await CountAs(Context("r1", Actor.Person("bob"))));
        Assert.Equal(3, await CountAs(Context("r1", Actor.System("ada"))));
        Assert.Equal(4, await CountAs(Context("r1", tenant: "north")));
        Assert.Equal(5, await CountAs(Context("r1", tenant: "south")));
        Assert.Equal(4, await CountAs(Context("r1", tenant: "north")));
        var otherCommand = await stub.SendAsync(
            new PostNoteThenNestCommand(text, NestedFailure.None),
            Context("r1"),
            TestContext.Current.CancellationToken
        );

        Assert.True(otherCommand.IsSuccess);
        Assert.Equal(1L, await Fixture.ScalarAsync<long>($"select count(*) from stub.notes where text = 'nested {text}'"));
        Assert.Equal(5, Runs.Of(text));
        Assert.Equal(
            4L,
            await Fixture.ScalarAsync<long>($"select count(*) from stub.pouryakit_idempotency_keys where tenant = '{IdempotencyTable.NoTenant}'")
        );
    }

    [Fact]
    public async Task A_concurrent_repeat_waits_for_the_first_operation_then_replays_its_committed_success()
    {
        var text = $"note {Guid.CreateVersion7()}";

        var (first, repeat) = await RaceAsync(text, firstFails: false);

        Assert.Equal(first.Value, repeat.Value);
        Assert.Equal(1, Runs.Of(text));
        Assert.Equal(1L, await Fixture.ScalarAsync<long>($"select count(*) from stub.notes where text = '{text}'"));
    }

    [Fact]
    public async Task A_concurrent_repeat_waits_for_the_first_operation_then_runs_when_it_rolled_back()
    {
        var text = $"note {Guid.CreateVersion7()}";

        var (first, repeat) = await RaceAsync(text, firstFails: true);

        Assert.Equal(CountNoteCommandHandler.Refused, Assert.Single(first.Errors));
        Assert.Equal(2, repeat.Value.Number);
        Assert.Equal(2, Runs.Of(text));
        Assert.Equal(1L, await Fixture.ScalarAsync<long>($"select count(*) from stub.notes where text = '{text}'"));
    }

    [Fact]
    public async Task A_repeated_import_answers_the_first_count_where_running_again_would_answer_0()
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var category = Guid.CreateVersion7();
        await catalog.SendAsync(new CreateCategoryCommand(category, "Lamps"), TestContext.Current.CancellationToken);
        var import = new ImportProductsCommand(category, ["Desk lamp", "Floor lamp"]);

        var first = await catalog.SendAsync(import, Context("r1"), TestContext.Current.CancellationToken);
        var repeat = await Fixture.AssertNothingSentAsync(
            () => catalog.SendAsync(import, Context("r1"), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(2, first.Value);
        Assert.Equal(2, repeat.Value);
        Assert.Equal(2L, await Fixture.ScalarAsync<long>("select count(*) from catalog.products"));
    }

    [Fact]
    public async Task A_repeated_idempotency_key_answers_the_first_status_and_body_without_creating_or_sending_anything()
    {
        using var client = Fixture.Client("ada");
        var category = Guid.CreateVersion7();
        await Fixture.Gateway<CatalogModule>().SendAsync(new CreateCategoryCommand(category, "Lamps"), TestContext.Current.CancellationToken);
        var product = Guid.CreateVersion7();
        var body = new { productId = product, categoryId = category, name = "Desk lamp" };
        var key = IdempotentRequests.NewKey();

        var (first, _) = await Fixture.WaitForDeliveriesAsync(
            () => client.PostAsJsonAsync("/catalog/products", body, key, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken
        );
        var repeat = await Fixture.AssertNothingSentAsync(
            () => client.PostAsJsonAsync("/catalog/products", body, key, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, repeat.StatusCode);
        Assert.Equal($"/catalog/v2/products/{product}", repeat.Headers.Location?.OriginalString);
        Assert.Equal(first.Headers.Location, repeat.Headers.Location);
        Assert.Equal($$"""{"id":"{{product}}"}""", await repeat.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            await repeat.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        Assert.Equal(1L, await Fixture.ScalarAsync<long>("select count(*) from catalog.products"));
    }

    // A blank tenant claim is no tenant at the HTTP boundary, so its request and one without the claim are the same
    // caller's, with one key, and neither is a tenant that collides with the empty string the key stores for no tenant.
    [Fact]
    public async Task A_blank_tenant_claim_runs_without_a_tenant_and_shares_the_key_of_a_caller_without_one()
    {
        using var blank = Fixture.Client("ada", tenant: TestAuthentication.BlankTenant);
        using var noTenant = Fixture.Client("ada");
        var category = Guid.CreateVersion7();
        await Fixture.Gateway<CatalogModule>().SendAsync(new CreateCategoryCommand(category, "Lamps"), TestContext.Current.CancellationToken);
        var body = new { productId = Guid.CreateVersion7(), categoryId = category, name = "Desk lamp" };
        var key = IdempotentRequests.NewKey();

        var seen = await blank.GetFromJsonAsync<SeenContext>("/stub/context", TestContext.Current.CancellationToken);
        var first = await blank.PostAsJsonAsync("/catalog/products", body, key, TestContext.Current.CancellationToken);
        var repeat = await noTenant.PostAsJsonAsync("/catalog/products", body, key, TestContext.Current.CancellationToken);

        Assert.Null(seen!.Tenant);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, repeat.StatusCode);
        Assert.Equal(first.Headers.Location, repeat.Headers.Location);
        Assert.Equal([[IdempotencyTable.NoTenant]], await Fixture.RowsAsync("select tenant from catalog.pouryakit_idempotency_keys"));
        Assert.Equal(1L, await Fixture.ScalarAsync<long>("select count(*) from catalog.products"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" padded ")]
    public async Task A_request_without_a_readable_idempotency_key_answers_400_as_a_problem_response_and_creates_nothing(string? key)
    {
        using var client = Fixture.Client("ada");
        var category = Guid.CreateVersion7();
        await Fixture.Gateway<CatalogModule>().SendAsync(new CreateCategoryCommand(category, "Lamps"), TestContext.Current.CancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/catalog/products")
        {
            Content = JsonContent.Create(new { productId = Guid.CreateVersion7(), categoryId = category, name = "Lamp" }),
        };
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation(IdempotentRequests.Header, key);
        }

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from catalog.products"));
    }

    private static OperationContext Context(string requestId, Actor? actor = null, string? tenant = null) =>
        new(actor ?? Actor.Person("ada"), tenant) { RequestId = requestId };

    // Two Operations with one key: the first holds its uncommitted key while the repeat is shown waiting on that row in
    // PostgreSQL, with its handler not yet run; then the first ends, committing or rolling back.
    private async Task<(Result<CountedNote> First, Result<CountedNote> Repeat)> RaceAsync(string text, bool firstFails)
    {
        var stub = Fixture.Gateway<StubModule>();
        var hold = new Hold();
        var first = stub.SendAsync(new CountNoteCommand(text, hold, firstFails), Context("r1"), TestContext.Current.CancellationToken);
        await hold.Changed.Task.WaitAsync(TestContext.Current.CancellationToken);

        var repeat = stub.SendAsync(new CountNoteCommand(text), Context("r1"), TestContext.Current.CancellationToken);
        await Fixture.WaitUntilAsync(
            "exists (select 1 from pg_stat_activity where wait_event_type = 'Lock' and query like 'insert into \"stub\".pouryakit_idempotency_keys%')",
            "The repeat never waited on the key the first Operation holds."
        );
        Assert.False(repeat.IsCompleted);
        Assert.Equal(1, Runs.Of(text));

        hold.Released.SetResult();
        return (await first, await repeat);
    }
}
