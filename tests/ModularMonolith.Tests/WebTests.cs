using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

// Each request goes through the Host's real HTTP pipeline: authentication, the Module route groups, the endpoint, the
// Gateway and PostgreSQL.
public sealed class WebTests(AppFixture app) : HostTest<AppFixture>(app)
{
    [Fact]
    public async Task A_success_answers_with_the_status_and_body_its_endpoint_chose()
    {
        using var client = Fixture.Client("ada");
        var category = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();

        var categoryCreated = await client.PostAsJsonAsync(
            "/catalog/categories",
            new { categoryId = category, name = "Lamps" },
            TestContext.Current.CancellationToken
        );
        var productCreated = await client.PostAsJsonAsync(
            "/catalog/products",
            new { productId = product, categoryId = category, name = "Desk lamp" },
            TestContext.Current.CancellationToken
        );
        var found = await client.GetAsync(productCreated.Headers.Location, TestContext.Current.CancellationToken);
        var listed = await client.GetAsync("/catalog/products", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, categoryCreated.StatusCode);
        Assert.Equal($"\"{category}\"", await BodyAsync(categoryCreated));
        Assert.Equal(HttpStatusCode.Created, productCreated.StatusCode);
        Assert.Equal($"/catalog/products/{product}", productCreated.Headers.Location?.OriginalString);
        Assert.Equal($$"""{"id":"{{product}}"}""", await BodyAsync(productCreated));
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.Equal($$"""{"id":"{{product}}","categoryId":"{{category}}","name":"Desk lamp"}""", await BodyAsync(found));
        // The list's ids are typed IDs, written as their bare values.
        Assert.Equal($$"""[{"id":"{{product}}","categoryId":"{{category}}","name":"Desk lamp"}]""", await BodyAsync(listed));
    }

    [Fact]
    public async Task An_empty_id_and_an_empty_name_answer_400_listing_every_validation_failure_and_nothing_is_saved()
    {
        using var client = Fixture.Client("ada");

        var response = await client.PostAsJsonAsync(
            "/catalog/products",
            new { productId = Guid.Empty, categoryId = Guid.CreateVersion7(), name = "" },
            TestContext.Current.CancellationToken
        );

        var problem = await ProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("https://tools.ietf.org/html/rfc9110#section-15.5.1", problem.GetProperty("type").GetString());
        Assert.Equal("Bad Request", problem.GetProperty("title").GetString());
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal(
            """[{"code":"NotEmptyValidator","message":"'Product Id' must not be empty.","target":"ProductId"},{"code":"NotEmptyValidator","message":"'Name' must not be empty.","target":"Name"}]""",
            problem.GetProperty("errors").GetRawText()
        );
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from catalog.products"));
    }

    [Fact]
    public async Task A_missing_product_answers_404_with_its_error()
    {
        using var client = Fixture.Client("ada");

        var response = await client.GetAsync($"/catalog/products/{Guid.CreateVersion7()}", TestContext.Current.CancellationToken);

        var problem = await ProblemAsync(response, HttpStatusCode.NotFound);
        Assert.Equal(
            """[{"code":"Product.NotFound","message":"The product does not exist."}]""",
            problem.GetProperty("errors").GetRawText()
        );
    }

    // A conflict a handler returns itself: the Stub Module's rejected note.
    [Fact]
    public async Task A_conflict_error_answers_409_with_its_error()
    {
        using var client = Fixture.Client("ada");

        var response = await client.PostAsync("/stub/notes/rejected", null, TestContext.Current.CancellationToken);

        var problem = await ProblemAsync(response, HttpStatusCode.Conflict);
        Assert.Equal(
            """[{"code":"Note.Rejected","message":"The note was rejected after it was written."}]""",
            problem.GetProperty("errors").GetRawText()
        );
    }

    // A conflict the database finds: the unique index on the name, which Catalog maps to ProductErrors.NameInUse.
    [Fact]
    public async Task Creating_a_product_with_a_name_in_use_answers_409_with_its_error_and_writes_nothing()
    {
        using var client = Fixture.Client("ada");
        var category = Guid.CreateVersion7();
        await client.PostAsJsonAsync(
            "/catalog/categories",
            new { categoryId = category, name = "Lamps" },
            TestContext.Current.CancellationToken
        );
        var lamp = Guid.CreateVersion7();
        await client.PostAsJsonAsync(
            "/catalog/products",
            new { productId = lamp, categoryId = category, name = "Desk lamp" },
            TestContext.Current.CancellationToken
        );

        var response = await client.PostAsJsonAsync(
            "/catalog/products",
            new { productId = Guid.CreateVersion7(), categoryId = category, name = "Desk lamp" },
            TestContext.Current.CancellationToken
        );

        var problem = await ProblemAsync(response, HttpStatusCode.Conflict);
        Assert.Equal(
            """[{"code":"Product.NameInUse","message":"Another product already has this name."}]""",
            problem.GetProperty("errors").GetRawText()
        );
        Assert.Equal([[lamp]], await Fixture.RowsAsync("select id from catalog.products"));
        Assert.Equal(1, await Fixture.ScalarAsync<int>("select product_count from catalog.categories"));
    }

    [Fact]
    public async Task Errors_of_several_kinds_answer_with_the_most_severe_kinds_status_and_list_every_error()
    {
        using var client = Fixture.Client("ada");

        var response = await client.GetAsync("/stub/mixed-errors", TestContext.Current.CancellationToken);

        var problem = await ProblemAsync(response, HttpStatusCode.NotFound);
        Assert.Equal(
            MixedErrorsEndpoint.Errors.Select(error => error.Code),
            problem.GetProperty("errors").EnumerateArray().Select(error => error.GetProperty("code").GetString())
        );
    }

    [Fact]
    public async Task An_unexpected_exception_answers_500_as_a_problem_response_without_its_message_and_is_logged()
    {
        using var client = Fixture.Client("ada");
        int LoggedByWeb() =>
            Fixture.Logs.Entries.Count(entry =>
                entry is { Category: "PouryaKit.Web.UnexpectedExceptionHandler", Level: LogLevel.Error }
                && entry.Exception?.Message == "The note handler threw."
            );
        var loggedBefore = LoggedByWeb();

        var response = await client.PostAsync("/stub/notes/throwing", null, TestContext.Current.CancellationToken);

        var problem = await ProblemAsync(response, HttpStatusCode.InternalServerError);
        Assert.Equal(500, problem.GetProperty("status").GetInt32());
        var body = problem.GetRawText();
        Assert.DoesNotContain("The note handler threw", body);
        Assert.DoesNotContain(nameof(InvalidOperationException), body);
        Assert.Equal(loggedBefore + 1, LoggedByWeb());
    }

    [Fact]
    public async Task A_module_endpoint_refuses_an_anonymous_caller_and_a_public_endpoint_runs_it_as_its_named_actor()
    {
        using var anonymous = Fixture.Client();
        using var ada = Fixture.Client("ada");

        var refused = await anonymous.PostAsJsonAsync(
            "/catalog/products",
            new { productId = Guid.CreateVersion7(), categoryId = Guid.CreateVersion7(), name = "Lamp" },
            TestContext.Current.CancellationToken
        );
        var listed = await anonymous.GetAsync("/catalog/products", TestContext.Current.CancellationToken);
        var anonymousContext = await ContextAsync(anonymous, "/stub/public/context");
        var adaContext = await ContextAsync(ada, "/stub/public/context");

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Equal(ReadOperationContextEndpoint.Visitor.Id, anonymousContext.Actor.Id);
        Assert.Equal(ActorKind.System, anonymousContext.Actor.Kind);
        // An authenticated caller of a public endpoint still runs as themselves.
        Assert.Equal("ada", adaContext.Actor.Id);
        Assert.Equal(ActorKind.Person, adaContext.Actor.Kind);
    }

    [Fact]
    public async Task The_handler_reads_the_authenticated_caller_as_the_actor_of_a_new_chain_of_work()
    {
        using var client = Fixture.Client("ada");

        var context = await ContextAsync(client, "/stub/context");

        Assert.Equal("ada", context.Actor.Id);
        Assert.Equal(ActorKind.Person, context.Actor.Kind);
        Assert.Equal(context.OperationId, context.CorrelationId);
        Assert.Null(context.CausationId);
    }

    [Fact]
    public async Task Each_modules_endpoints_are_mapped_under_its_own_route_group()
    {
        using var client = Fixture.Client("ada");

        async Task<HttpStatusCode> StatusOf(string path) =>
            (await client.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode;

        Assert.Equal(HttpStatusCode.OK, await StatusOf("/catalog/products"));
        Assert.Equal(HttpStatusCode.OK, await StatusOf("/stub/context"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf("/products"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf("/context"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf("/stub/products"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf("/catalog/context"));
    }

    private static Task<string> BodyAsync(HttpResponseMessage response) =>
        response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await BodyAsync(response));
        return problem.RootElement.Clone();
    }

    private static async Task<SeenContext> ContextAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SeenContext>(TestContext.Current.CancellationToken))!;
    }

    private sealed record SeenContext(Guid OperationId, SeenActor Actor, Guid CorrelationId, Guid? CausationId);

    private sealed record SeenActor(string Id, ActorKind Kind);
}
