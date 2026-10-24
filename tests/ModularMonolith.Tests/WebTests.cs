using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Testing;
using static ModularMonolith.Tests.Fixtures.ProblemResponses;

namespace ModularMonolith.Tests;

// Each request goes through the Host's real HTTP pipeline: authentication, the Module route groups, the endpoint, the
// Gateway and PostgreSQL.
public sealed class WebTests(AppFixture app) : HostTest<AppFixture>(app)
{
    [Fact]
    public async Task A_success_answers_with_the_status_and_body_its_endpoint_chose()
    {
        using var client = Fixture.Client("ada", AppFixture.TestTenant);
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
            IdempotentRequests.NewKey(),
            TestContext.Current.CancellationToken
        );
        var found = await client.GetAsync(productCreated.Headers.Location, TestContext.Current.CancellationToken);
        var listed = await client.GetAsync("/catalog/products", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, categoryCreated.StatusCode);
        Assert.Equal($"\"{category}\"", await BodyAsync(categoryCreated));
        Assert.Equal(HttpStatusCode.Created, productCreated.StatusCode);
        Assert.Equal($"/catalog/v2/products/{product}", productCreated.Headers.Location?.OriginalString);
        Assert.Equal($$"""{"id":"{{product}}"}""", await BodyAsync(productCreated));
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.Equal($$"""{"id":"{{product}}","categoryId":"{{category}}","name":"Desk lamp"}""", await BodyAsync(found));
        // The list is a Page; its ids are typed IDs, written as their bare values.
        Assert.Equal(
            $$"""{"items":[{"id":"{{product}}","categoryId":"{{category}}","name":"Desk lamp"}],"number":1,"size":20,"total":1,"totalPages":1,"hasNext":false,"hasPrevious":false}""",
            await BodyAsync(listed)
        );
    }

    [Fact]
    public async Task An_empty_id_and_an_empty_name_answer_400_listing_every_validation_failure_and_nothing_is_saved()
    {
        using var client = Fixture.Client("ada", AppFixture.TestTenant);

        var response = await client.PostAsJsonAsync(
            "/catalog/products",
            new { productId = Guid.Empty, categoryId = Guid.CreateVersion7(), name = "" },
            IdempotentRequests.NewKey(),
            TestContext.Current.CancellationToken
        );

        var problem = await ProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("https://tools.ietf.org/html/rfc9110#section-15.5.1", problem.GetProperty("type").GetString());
        Assert.Equal("Bad Request", problem.GetProperty("title").GetString());
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        // Messages follow the current UI culture; codes and targets do not.
        Assert.Equal(
            [("NotEmptyValidator", "ProductId"), ("NotEmptyValidator", "Name")],
            problem
                .GetProperty("errors")
                .EnumerateArray()
                .Select(error => (error.GetProperty("code").GetString(), error.GetProperty("target").GetString()))
        );
        Assert.All(
            problem.GetProperty("errors").EnumerateArray(),
            error => Assert.NotEmpty(error.GetProperty("message").GetString()!)
        );
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from catalog.products"));
    }

    // HTTP bodies keep the lenient reading; only envelopes are read strictly (#60). A member the body lacks binds as its
    // default and reaches the validator, where a strict read would refuse the body before any validation.
    [Fact]
    public async Task A_body_without_a_member_binds_it_as_its_default_and_validation_answers_400_for_it()
    {
        using var client = Fixture.Client("ada", AppFixture.TestTenant);

        var response = await client.PostAsJsonAsync(
            "/catalog/products",
            new { productId = Guid.CreateVersion7(), categoryId = Guid.CreateVersion7() },
            IdempotentRequests.NewKey(),
            TestContext.Current.CancellationToken
        );

        var problem = await ProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(
            [("NotEmptyValidator", "Name")],
            problem
                .GetProperty("errors")
                .EnumerateArray()
                .Select(error => (error.GetProperty("code").GetString(), error.GetProperty("target").GetString()))
        );
    }

    // A body that names a member twice, in its own case or another, cannot be read (#86): it is refused before any
    // validation, never read with its last value.
    [Theory]
    [InlineData("name")]
    [InlineData("Name")]
    public async Task A_body_that_names_a_member_twice_answers_400_as_a_problem_response_and_nothing_is_saved(string again)
    {
        using var client = Fixture.Client("ada", AppFixture.TestTenant);
        var category = Guid.CreateVersion7();
        using var body = new StringContent(
            $$"""{"categoryId":"{{category}}","name":"Lamps","{{again}}":"Chairs"}""",
            System.Text.Encoding.UTF8,
            "application/json"
        );

        var response = await client.PostAsync("/catalog/categories", body, TestContext.Current.CancellationToken);

        var problem = await ProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.False(problem.TryGetProperty("errors", out _));
        Assert.False(problem.TryGetProperty("detail", out _));
        Assert.Equal(0L, await Fixture.ScalarAsync<long>($"select count(*) from catalog.categories where id = '{category}'"));
    }

    [Fact]
    public async Task A_missing_product_answers_404_with_its_error()
    {
        using var client = Fixture.Client("ada", AppFixture.TestTenant);

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
        using var client = Fixture.Client("ada", AppFixture.TestTenant);
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
            IdempotentRequests.NewKey(),
            TestContext.Current.CancellationToken
        );

        var response = await client.PostAsJsonAsync(
            "/catalog/products",
            new { productId = Guid.CreateVersion7(), categoryId = category, name = "Desk lamp" },
            IdempotentRequests.NewKey(),
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

    // Each case builds a Host of its own in the environment: outside Development, ASP.NET Core answers these
    // differently unless the Kit makes them the same. None of them reaches the database.
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task An_exception_in_authentication_answers_500_as_a_problem_response_without_its_message(string environment)
    {
        await using var host = await StartHostAsync(environment);
        using var client = AppFixture.ClientOf(host, TestAuthentication.Throwing);

        var response = await client.GetAsync("/catalog/products", TestContext.Current.CancellationToken);

        var problem = await ProblemAsync(response, HttpStatusCode.InternalServerError);
        Assert.DoesNotContain(TestAuthentication.ThrownMessage, problem.GetRawText());
    }

    [Theory]
    [InlineData("Development", "POST", "/catalog/products", "{\"productId\":")]
    [InlineData("Production", "POST", "/catalog/products", "{\"productId\":")]
    [InlineData("Development", "GET", "/catalog/products/not-a-guid", null)]
    [InlineData("Production", "GET", "/catalog/products/not-a-guid", null)]
    public async Task A_request_that_cannot_be_read_answers_400_as_a_problem_response_and_is_not_logged_as_an_error(
        string environment,
        string method,
        string path,
        string? json
    )
    {
        var logs = new RecordedLogs();
        await using var host = await StartHostAsync(environment, logs);
        using var client = AppFixture.ClientOf(host, "ada");
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = json is null ? null : new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        // Creating a product requires one, so it is the body that cannot be read; reading a product ignores it.
        request.Headers.Add(IdempotentRequests.Header, IdempotentRequests.NewKey());

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var problem = await ProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        // The binding failure's own message, which quotes what the caller sent, stays out of the response.
        Assert.False(problem.TryGetProperty("detail", out _));
        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Error);
    }

    // Routing refuses both itself, before any endpoint or exception handler runs; the answer is a problem response
    // whatever the caller accepts.
    [Theory]
    [InlineData("POST", "text/plain", HttpStatusCode.UnsupportedMediaType, null)]
    [InlineData("PUT", "application/json", HttpStatusCode.MethodNotAllowed, null)]
    [InlineData("POST", "text/plain", HttpStatusCode.UnsupportedMediaType, "text/html")]
    [InlineData("PUT", "application/json", HttpStatusCode.MethodNotAllowed, "text/html")]
    public async Task A_request_routing_refuses_answers_its_status_as_a_problem_response_and_nothing_is_saved(
        string method,
        string contentType,
        HttpStatusCode status,
        string? accept
    )
    {
        using var client = Fixture.Client("ada");
        if (accept is not null)
        {
            client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        }

        using var request = new HttpRequestMessage(new HttpMethod(method), "/catalog/products")
        {
            Content = new StringContent(
                $$"""{"productId":"{{Guid.CreateVersion7()}}","categoryId":"{{Guid.CreateVersion7()}}","name":"Lamp"}""",
                System.Text.Encoding.UTF8,
                contentType
            ),
        };

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var problem = await ProblemAsync(response, status);
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from catalog.products"));
    }

    [Fact]
    public async Task A_problem_response_is_written_whatever_the_caller_accepts()
    {
        using var client = Fixture.Client("ada");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");

        var failed = await client.GetAsync("/stub/mixed-errors", TestContext.Current.CancellationToken);
        var threw = await client.PostAsync("/stub/notes/throwing", null, TestContext.Current.CancellationToken);

        Assert.Equal(404, (await ProblemAsync(failed, HttpStatusCode.NotFound)).GetProperty("status").GetInt32());
        Assert.Equal(500, (await ProblemAsync(threw, HttpStatusCode.InternalServerError)).GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task A_module_endpoint_that_allows_anonymous_callers_without_naming_an_actor_fails_the_host_at_start()
    {
        await using var host = AppFixture.ComposeHost(
            "Production",
            Fixture.ConnectionString,
            new RecordedLogs(),
            builder => builder.Services.AddSingleton<PlainAnonymousRoute>()
        );

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken)
        );

        Assert.Contains("allows anonymous callers but names no Actor", thrown.Message);
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
        var anonymousContext = await ContextAsync(anonymous, "/stub/public/context");
        var adaContext = await ContextAsync(ada, "/stub/public/context");

        await ProblemAsync(refused, HttpStatusCode.Unauthorized);
        Assert.Equal(ReadOperationContextEndpoint.Visitor.Id, anonymousContext.Actor.Id);
        Assert.Equal(ActorKind.System, anonymousContext.Actor.Kind);
        // An authenticated caller of a public endpoint still runs as themselves.
        Assert.Equal("ada", adaContext.Actor.Id);
        Assert.Equal(ActorKind.Person, adaContext.Actor.Kind);
    }

    [Fact]
    public async Task An_authenticated_caller_denied_authorization_answers_403_as_a_problem_response()
    {
        await using var host = AppFixture.ComposeHost(
            "Production",
            Fixture.ConnectionString,
            new RecordedLogs(),
            builder =>
                builder.Services.AddAuthorization(authorization =>
                    authorization.DefaultPolicy = new AuthorizationPolicyBuilder()
                        .RequireAuthenticatedUser()
                        .RequireClaim(ClaimTypes.NameIdentifier, "grace")
                        .Build()
                )
        );
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var allowed = AppFixture.ClientOf(host, "grace");
        using var denied = AppFixture.ClientOf(host, "ada");
        denied.DefaultRequestHeaders.Accept.ParseAdd("text/html");

        Assert.Equal(
            HttpStatusCode.OK,
            (await allowed.GetAsync("/stub/context", TestContext.Current.CancellationToken)).StatusCode
        );
        var response = await denied.GetAsync("/stub/context", TestContext.Current.CancellationToken);

        var problem = await ProblemAsync(response, HttpStatusCode.Forbidden);
        Assert.Equal(403, problem.GetProperty("status").GetInt32());
        Assert.False(problem.TryGetProperty("errors", out _));
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
    public async Task The_tenant_claim_fills_the_tenant_and_a_caller_without_it_runs_with_none()
    {
        using var acme = Fixture.Client("ada", tenant: "acme");
        using var noTenant = Fixture.Client("ada");

        Assert.Equal("acme", (await ContextAsync(acme, "/stub/context")).Tenant);
        Assert.Null((await ContextAsync(noTenant, "/stub/context")).Tenant);
    }

    [Fact]
    public async Task An_authenticated_caller_without_the_actor_claim_is_refused_with_a_500_problem_response()
    {
        using var client = Fixture.Client(TestAuthentication.WithoutActorClaim);

        var response = await client.GetAsync("/stub/context", TestContext.Current.CancellationToken);

        var problem = await ProblemAsync(response, HttpStatusCode.InternalServerError);
        Assert.DoesNotContain("claim", problem.GetRawText());
        Assert.Contains(
            Fixture.Logs.Entries,
            entry =>
                entry.Category == "PouryaKit.Web.UnexpectedExceptionHandler"
                && entry.Exception?.Message.Contains($"no {ClaimTypes.NameIdentifier} claim") == true
        );
    }

    [Fact]
    public async Task Each_modules_endpoints_are_mapped_under_its_own_route_group()
    {
        using var client = Fixture.Client("ada", AppFixture.TestTenant);

        async Task<HttpStatusCode> StatusOf(string path) =>
            (await client.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode;

        Assert.Equal(HttpStatusCode.OK, await StatusOf("/catalog/products"));
        Assert.Equal(HttpStatusCode.OK, await StatusOf("/stub/context"));
        await ProblemAsync(await client.GetAsync("/products", TestContext.Current.CancellationToken), HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf("/context"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf("/stub/products"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf("/catalog/context"));
    }

    private static Task<string> BodyAsync(HttpResponseMessage response) =>
        response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    private static async Task<SeenContext> ContextAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SeenContext>(TestContext.Current.CancellationToken))!;
    }

    private async Task<WebApplication> StartHostAsync(string environment, RecordedLogs? logs = null)
    {
        var host = AppFixture.ComposeHost(environment, Fixture.ConnectionString, logs ?? new RecordedLogs());
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }
}
