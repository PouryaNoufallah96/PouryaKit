using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Asp.Versioning;
using Asp.Versioning.ApiExplorer;
using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Features.CreateProduct;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Unversioned;
using PouryaKit.Cqrs;
using PouryaKit.Testing;
using PouryaKit.Web;
using PouryaKit.Web.OpenApi;
using static ModularMonolith.Tests.Fixtures.ProblemResponses;

namespace ModularMonolith.Tests;

// Catalog declares API version 1.0, deprecated with the sunset the sample's appsettings.json configures, and 2.0, which
// adds renaming a product. Each request goes through the Host's real HTTP pipeline.
public sealed partial class OpenApiTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private const string ConfiguredSunset = "Wed, 30 Jun 2027 00:00:00 GMT";
    private const string ConfiguredSunsetLink = "https://example.com/catalog/api-v1-sunset";

    #region version-by-url-or-header
    [Fact]
    public async Task A_version_is_selected_by_its_url_segment_or_by_the_api_version_header()
    {
        var lamp = await CreateLampAsync();
        using var client = Unversioned(Fixture.Client("ada", AppFixture.TestTenant));

        var renamedByUrl = await RenameAsync(client, $"/catalog/v2/products/{lamp}/name", null, "Desk lamp");
        var refusedByUrl = await RenameAsync(client, $"/catalog/v1/products/{lamp}/name", null, "Floor lamp");
        var renamedByHeader = await RenameAsync(client, $"/catalog/products/{lamp}/name", "2.0", "Reading lamp");
        var refusedByHeader = await RenameAsync(client, $"/catalog/products/{lamp}/name", "1.0", "Floor lamp");
        var readByUrl = await SendAsync(client, HttpMethod.Get, $"/catalog/v1/products/{lamp}", null);
        var readByHeader = await SendAsync(client, HttpMethod.Get, $"/catalog/products/{lamp}", "1.0");

        Assert.Equal(HttpStatusCode.NoContent, renamedByUrl.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, renamedByHeader.StatusCode);
        // Renaming arrived in 2.0: version 1.0 serves no such operation.
        await ProblemAsync(refusedByUrl, HttpStatusCode.NotFound);
        await ProblemAsync(refusedByHeader, HttpStatusCode.BadRequest);
        Assert.Equal("Reading lamp", await Fixture.ScalarAsync<string>($"select name from catalog.products where id = '{lamp}'"));
        Assert.Equal(HttpStatusCode.OK, readByUrl.StatusCode);
        Assert.Equal(HttpStatusCode.OK, readByHeader.StatusCode);
        Assert.Equal(["2.0"], readByUrl.Headers.GetValues("api-supported-versions"));
        Assert.Equal(["1.0"], readByUrl.Headers.GetValues("api-deprecated-versions"));
    }
    #endregion

    [Fact]
    public async Task A_request_naming_no_version_or_two_different_versions_is_refused_with_a_problem_response()
    {
        using var client = Unversioned(Fixture.Client("ada"));

        var unnamed = await SendAsync(client, HttpMethod.Get, "/catalog/products", null);
        var ambiguous = await SendAsync(client, HttpMethod.Get, "/catalog/v1/products", "2.0");

        await ProblemAsync(unnamed, HttpStatusCode.BadRequest);
        await ProblemAsync(ambiguous, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_deprecated_version_reports_its_configured_sunset_in_its_responses_and_its_document()
    {
        using var client = Unversioned(Fixture.Client("ada", AppFixture.TestTenant));

        var deprecated = await SendAsync(client, HttpMethod.Get, "/catalog/v1/products", null);
        var current = await SendAsync(client, HttpMethod.Get, "/catalog/v2/products", null);
        var v1 = await DocumentAsync(client, "v1");
        var v2 = await DocumentAsync(client, "v2");

        Assert.Equal(HttpStatusCode.OK, deprecated.StatusCode);
        Assert.Equal([ConfiguredSunset], deprecated.Headers.GetValues("Sunset"));
        Assert.Equal(
            [$"<{ConfiguredSunsetLink}>; rel=\"sunset\"; type=\"text/html\""],
            deprecated.Headers.GetValues("Link")
        );
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.False(current.Headers.Contains("Sunset"));
        // The notice names the configured date as it is, whatever the time now.
        Assert.Equal(
            $"This API version is deprecated. Its sunset date is {ConfiguredSunset}.\n\n### Links\n\n- {ConfiguredSunsetLink}\n",
            v1.GetProperty("info").GetProperty("description").GetString()
        );
        Assert.Equal(
            ConfiguredSunsetLink,
            v1.GetProperty("x-api-versioning").GetProperty("links")[0].GetProperty("url").GetString()
        );
        Assert.Equal("", v2.GetProperty("info").GetProperty("description").GetString());
        Assert.False(v2.TryGetProperty("x-api-versioning", out _));
    }

    [Fact]
    public async Task Each_supported_version_has_a_document_of_the_operations_it_serves()
    {
        using var client = Unversioned(Fixture.Client());

        var v1 = await DocumentAsync(client, "v1");
        var v2 = await DocumentAsync(client, "v2");

        Assert.Equal("1.0", v1.GetProperty("info").GetProperty("version").GetString());
        Assert.Equal(
            [
                "/catalog/categories post",
                "/catalog/events get",
                "/catalog/products get",
                "/catalog/products post",
                "/catalog/products/ask post",
                "/catalog/products/{productId} get",
                "/catalog/v1/categories post",
                "/catalog/v1/events get",
                "/catalog/v1/products get",
                "/catalog/v1/products post",
                "/catalog/v1/products/ask post",
                "/catalog/v1/products/{productId} get",
            ],
            CatalogOperations(v1).Select(operation => operation.Name)
        );
        Assert.All(CatalogOperations(v1), operation => Assert.True(operation.Body.GetProperty("deprecated").GetBoolean()));
        Assert.Equal("2.0", v2.GetProperty("info").GetProperty("version").GetString());
        Assert.Equal(
            [
                "/catalog/categories post",
                "/catalog/events get",
                "/catalog/products get",
                "/catalog/products post",
                "/catalog/products/ask post",
                "/catalog/products/{productId} get",
                "/catalog/products/{productId}/name put",
                "/catalog/v2/categories post",
                "/catalog/v2/events get",
                "/catalog/v2/products get",
                "/catalog/v2/products post",
                "/catalog/v2/products/ask post",
                "/catalog/v2/products/{productId} get",
                "/catalog/v2/products/{productId}/name put",
            ],
            CatalogOperations(v2).Select(operation => operation.Name)
        );
        Assert.All(CatalogOperations(v2), operation => Assert.False(operation.Body.TryGetProperty("deprecated", out _)));
        // An unversioned path takes its version from the header.
        Assert.Contains(
            v2.GetProperty("paths").GetProperty("/catalog/products/{productId}").GetProperty("get").GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString() == "api-version" && parameter.GetProperty("in").GetString() == "header"
        );
    }

    [Fact]
    public async Task Each_operation_requires_the_bearer_scheme_unless_it_allows_anonymous_callers()
    {
        var lamp = await CreateLampAsync();
        // The Host's own composition, whose default scheme is JwtBearer's, not the fixture's test scheme.
        await using var host = AppFixture.ComposeHost(
            "Development",
            Fixture.ConnectionString,
            new RecordedLogs(),
            builder => builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        );
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var anonymous = Unversioned(AppFixture.ClientOf(host));

        var v2 = await DocumentAsync(anonymous, "v2");
        var read = await SendAsync(anonymous, HttpMethod.Get, $"/catalog/v2/products/{lamp}", null);
        var visited = await SendAsync(anonymous, HttpMethod.Get, "/stub/public/context", "2.0");

        Assert.Equal(
            """{"type":"http","scheme":"bearer"}""",
            v2.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer").GetRawText().Replace(" ", "").Replace("\n", "")
        );
        Assert.Equal(
            [
                ("/catalog/v2/categories post", true),
                ("/catalog/v2/events get", true),
                ("/catalog/v2/products get", true),
                ("/catalog/v2/products post", true),
                ("/catalog/v2/products/ask post", true),
                ("/catalog/v2/products/{productId} get", true),
                ("/catalog/v2/products/{productId}/name put", true),
                ("/stub/context get", true),
                ("/stub/kiosk/context get", false),
                ("/stub/public/context get", false),
            ],
            Operations(v2)
                .Where(operation => operation.Name.StartsWith("/catalog/v2/") || operation.Name.Contains("/context "))
                .Select(operation => (operation.Name, RequiresBearer(operation.Body)))
        );
        // The document says what the pipeline does: the Stub's public endpoint serves an anonymous caller, reading a
        // product does not.
        Assert.Equal(HttpStatusCode.OK, visited.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
    }

    [Fact]
    public async Task An_operation_whose_endpoint_requires_an_idempotency_key_declares_the_required_header_in_every_version()
    {
        using var client = Unversioned(Fixture.Client());

        var v1 = await DocumentAsync(client, "v1");
        var v2 = await DocumentAsync(client, "v2");

        // Creating a product requires one; no other Catalog operation, creating a category included, does.
        Assert.Equal(
            ["/catalog/products post", "/catalog/v1/products post"],
            CatalogOperations(v1).Where(operation => IdempotencyKeyOf(operation.Body) is not null).Select(operation => operation.Name)
        );
        Assert.Equal(
            ["/catalog/products post", "/catalog/v2/products post"],
            CatalogOperations(v2).Where(operation => IdempotencyKeyOf(operation.Body) is not null).Select(operation => operation.Name)
        );
        var header = IdempotencyKeyOf(v2.GetProperty("paths").GetProperty("/catalog/v2/products").GetProperty("post"))!.Value;
        Assert.Equal("header", header.GetProperty("in").GetString());
        Assert.True(header.GetProperty("required").GetBoolean());
        Assert.Equal("string", header.GetProperty("schema").GetProperty("type").GetString());
        Assert.Contains("1 to 256 visible ASCII characters, with spaces only between them", header.GetProperty("description").GetString());
    }

    [Fact]
    public async Task An_operation_protected_by_a_scheme_the_document_cannot_state_carries_no_requirement()
    {
        using var client = Unversioned(Fixture.Client());

        // The fixture's default scheme is its test scheme, not a JWT bearer one.
        var v2 = await DocumentAsync(client, "v2");

        Assert.All(Operations(v2), operation => Assert.False(operation.Body.TryGetProperty("security", out _), operation.Name));
        Assert.False(v2.TryGetProperty("components", out var components) && components.TryGetProperty("securitySchemes", out _));
    }

    // The Host registers JWT bearer beside the fixture's default test scheme, which the document cannot state; only the
    // endpoint whose policy names the bearer scheme requires it.
    [Fact]
    public async Task An_operation_whose_policy_names_a_scheme_requires_that_scheme_and_not_the_default_one()
    {
        await using var host = AppFixture.ComposeHost("Development", Fixture.ConnectionString, new RecordedLogs());
        host.MapGet("/bearer-only", () => "bearer")
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme });
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var anonymous = Unversioned(AppFixture.ClientOf(host));

        var v2 = await DocumentAsync(anonymous, "v2");

        Assert.Equal(
            [("/bearer-only get", true)],
            Operations(v2)
                .Where(operation => operation.Body.TryGetProperty("security", out _))
                .Select(operation => (operation.Name, RequiresBearer(operation.Body)))
        );
        Assert.Equal(
            """{"type":"http","scheme":"bearer"}""",
            v2.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer").GetRawText().Replace(" ", "").Replace("\n", "")
        );
    }

    // Asp.Versioning appends its document delegate on every call; the Kit's second call adds none, so each transformer
    // runs once.
    [Fact]
    public async Task A_second_registration_of_the_api_versions_and_documents_adds_nothing()
    {
        await using var host = AppFixture.ComposeHost(
            "Development",
            Fixture.ConnectionString,
            new RecordedLogs(),
            builder => builder.Services.AddPouryaKitOpenApi()
        );
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var client = Unversioned(AppFixture.ClientOf(host));

        var v1 = await DocumentAsync(client, "v1");

        Assert.Equal(
            ["/catalog/products post", "/catalog/v1/products post"],
            CatalogOperations(v1).Where(operation => IdempotencyKeyOf(operation.Body) is not null).Select(operation => operation.Name)
        );
    }

    [Fact]
    public async Task A_created_products_location_names_the_version_the_request_selected_and_is_followed_as_it_is()
    {
        var category = Guid.CreateVersion7();
        await Fixture.Gateway<CatalogModule>().SendAsync(new CreateCategoryCommand(category, "Lamps"), TestContext.Current.CancellationToken);
        using var client = Unversioned(Fixture.Client("ada", AppFixture.TestTenant));
        var byUrl = Guid.CreateVersion7();
        var byHeader = Guid.CreateVersion7();

        var createdByUrl = await SendAsync(client, HttpMethod.Post, "/catalog/v2/products", null, JsonContent.Create(new { productId = byUrl, categoryId = category, name = "Desk lamp" }), IdempotentRequests.NewKey());
        var readByUrl = await SendAsync(client, HttpMethod.Get, createdByUrl.Headers.Location!.OriginalString, null);
        var createdByHeader = await SendAsync(client, HttpMethod.Post, "/catalog/products", "1.0", JsonContent.Create(new { productId = byHeader, categoryId = category, name = "Floor lamp" }), IdempotentRequests.NewKey());
        var readByHeader = await SendAsync(client, HttpMethod.Get, createdByHeader.Headers.Location!.OriginalString, "1.0");

        Assert.Equal(HttpStatusCode.Created, createdByUrl.StatusCode);
        Assert.Equal($"/catalog/v2/products/{byUrl}", createdByUrl.Headers.Location.OriginalString);
        Assert.Equal(HttpStatusCode.OK, readByUrl.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createdByHeader.StatusCode);
        Assert.Equal($"/catalog/v1/products/{byHeader}", createdByHeader.Headers.Location.OriginalString);
        Assert.Equal(HttpStatusCode.OK, readByHeader.StatusCode);
    }

    [Fact]
    public async Task A_declaration_for_a_route_group_no_module_maps_fails_the_host_at_start()
    {
        await using var host = PlainHost(services =>
        {
            services.AddPouryaKitOpenApi();
            services.AddPouryaKitApiVersions("/plians", versions => versions.HasApiVersion(new ApiVersion(1, 0)));
        });

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken)
        );

        Assert.StartsWith(
            "API versions are declared for the route group /plians, but no Module's endpoints are mapped under it.",
            thrown.Message
        );
    }

    // A Web Host with no Modules and no Wolverine reaches the dispatcher but not the Kit's entry: it does not start
    // unverified (#62 review finding F2).
    [Fact]
    public async Task A_web_host_that_never_installs_the_kits_checks_fails_at_start_naming_the_entry()
    {
        await using var host = PlainHost(
            services =>
            {
                services.AddPouryaKitOpenApi();
                services.AddPouryaKitApiVersions("/plians", versions => versions.HasApiVersion(new ApiVersion(1, 0)));
            },
            installChecks: false
        );

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken)
        );

        Assert.Equal(
            "The Kit's composition checks were never installed in this Host, so its container was not validated when it was built and none of its composition or startup checks ran. Call AddPouryaKitCompositionChecks() on the Host builder; AddPouryaKitModules and UsePouryaKitWolverine call it.",
            thrown.Message
        );
    }

    [Fact]
    public async Task Scalar_lists_the_document_of_every_supported_version()
    {
        using var client = Unversioned(Fixture.Client());

        var page = await client.GetAsync("/scalar/", TestContext.Current.CancellationToken);
        var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var sources = JsonDocument.Parse(Sources().Match(html).Groups[1].Value).RootElement;

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            [("v1", "openapi/v1.json"), ("v2", "openapi/v2.json")],
            sources.EnumerateArray().Select(source => (source.GetProperty("title").GetString(), source.GetProperty("url").GetString()))
        );
        foreach (var (source, version) in sources.EnumerateArray().Zip(["1.0", "2.0"]))
        {
            var document = await client.GetFromJsonAsync<JsonElement>(
                $"/{source.GetProperty("url").GetString()}",
                TestContext.Current.CancellationToken
            );
            Assert.Equal(version, document.GetProperty("info").GetProperty("version").GetString());
        }
    }

    [Fact]
    public async Task A_host_that_does_not_register_api_versions_serves_a_module_that_declares_none_unversioned()
    {
        await using var host = PlainHost();
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var client = AppFixture.ClientOf(host, "ada");

        var read = await client.GetAsync("/plain/actor", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("ada", await read.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Null(host.Services.GetService<IApiVersionDescriptionProvider>());
        Assert.Null(host.Services.GetService<IModuleRouteGroups>());
    }

    [Fact]
    public void A_module_that_declares_api_versions_fails_the_host_that_does_not_register_them()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            PlainHost(services =>
                services.AddPouryaKitApiVersions("/plain", versions => versions.HasApiVersion(new ApiVersion(1, 0)))
            )
        );

        Assert.Equal(
            "The Module mapped under /plain declares API versions, but the Host does not register them. Call AddPouryaKitOpenApi on the Host's services.",
            thrown.Message
        );
    }

    // A Host of PouryaKit.Web alone: no Modules and no API versioning, with one route group of an endpoint that
    // declares no version.
    private static WebApplication PlainHost(Action<IServiceCollection>? configure = null, bool installChecks = true)
    {
        var builder = WebApplication.CreateBuilder(["--environment", "Development"]);
        builder.WebHost.UseTestServer();
        builder
            .Services.AddAuthentication(TestAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthentication>(TestAuthentication.SchemeName, null);
        builder.Services.AddAuthorization();
        builder.Services.AddPouryaKitCqrs();
        builder.Services.AddPouryaKitWeb();
        builder.Services.AddPouryaKitEndpoints("/plain", typeof(UnversionedEndpoints).Assembly);
        configure?.Invoke(builder.Services);
        if (installChecks)
        {
            builder.AddPouryaKitCompositionChecks();
        }

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        try
        {
            app.MapPouryaKitModuleEndpoints();
        }
        catch
        {
            ((IDisposable)app).Dispose();
            throw;
        }

        return app;
    }

    private async Task<Guid> CreateLampAsync()
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var lamps = Guid.CreateVersion7();
        var lamp = Guid.CreateVersion7();
        await catalog.SendAsync(new CreateCategoryCommand(lamps, "Lamps"), TestContext.Current.CancellationToken);
        await catalog.SendAsync(new CreateProductCommand(lamp, lamps, $"Lamp {lamp}"), TestContext.Current.CancellationToken);
        return lamp;
    }

    // The fixture's clients name the current version in the header; these tests name their own.
    private static HttpClient Unversioned(HttpClient client)
    {
        client.DefaultRequestHeaders.Remove(WebOpenApiServiceCollectionExtensions.ApiVersionHeader);
        return client;
    }

    private static Task<HttpResponseMessage> RenameAsync(HttpClient client, string path, string? version, string name) =>
        SendAsync(client, HttpMethod.Put, path, version, JsonContent.Create(new { name }));

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? version,
        HttpContent? content = null,
        string? idempotencyKey = null
    )
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (version is not null)
        {
            request.Headers.Add(WebOpenApiServiceCollectionExtensions.ApiVersionHeader, version);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add(IdempotentRequests.Header, idempotencyKey);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> DocumentAsync(HttpClient client, string name)
    {
        var response = await client.GetAsync($"/openapi/{name}.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private static IEnumerable<(string Name, JsonElement Body)> Operations(JsonElement document) =>
        document
            .GetProperty("paths")
            .EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject().Select(operation => ($"{path.Name} {operation.Name}", operation.Value)))
            .OrderBy(operation => operation.Item1, StringComparer.Ordinal);

    private static List<(string Name, JsonElement Body)> CatalogOperations(JsonElement document) =>
        [.. Operations(document).Where(operation => operation.Name.StartsWith("/catalog/", StringComparison.Ordinal))];

    private static JsonElement? IdempotencyKeyOf(JsonElement operation) =>
        operation.TryGetProperty("parameters", out var parameters)
            ? parameters.EnumerateArray().Where(parameter => parameter.GetProperty("name").GetString() == IdempotencyKeyRequirement.Header).Cast<JsonElement?>().SingleOrDefault()
            : null;

    private static bool RequiresBearer(JsonElement operation) =>
        operation.TryGetProperty("security", out var security)
        && security.GetRawText().Replace(" ", "").Replace("\n", "") == """[{"Bearer":[]}]""";

    [GeneratedRegex("\"sources\":(\\[.*?\\])")]
    private static partial Regex Sources();
}
