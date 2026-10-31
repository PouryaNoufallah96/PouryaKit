using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ModularMonolith.Host;
using ModularMonolith.Tests;
using PouryaKit.Cqrs;
using PouryaKit.Security;
using PouryaKit.Testing;
using Wolverine;

[assembly: AssemblyFixture(typeof(App))]
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

namespace ModularMonolith.Tests;

// The Host over HTTP, as Program composes it: Catalog's Features through the pipeline, the Unit of Work and PostgreSQL,
// with a bearer token the Host verifies, and the composition checks it runs when it is built.
public sealed class HostTests(App app) : HostTest<App>(app)
{
    [Fact]
    public async Task A_product_created_with_an_idempotency_key_is_read_and_listed_and_its_repeat_creates_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = Fixture.ClientAs("ada");
        var category = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        var body = new
        {
            productId = product,
            categoryId = category,
            name = "Desk lamp",
        };
        var key = Guid.CreateVersion7().ToString();

        var lamps = await client.PostAsJsonAsync(
            "/catalog/categories",
            new { categoryId = category, name = "Lamps" },
            cancellationToken
        );
        var first = await App.PostAsync(client, "/catalog/products", body, key, cancellationToken);
        var repeat = await App.PostAsync(client, "/catalog/products", body, key, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, lamps.StatusCode);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal($"/catalog/v2/products/{product}", first.Headers.Location?.OriginalString);
        Assert.Equal(
            $$"""{"id":"{{product}}"}""",
            await first.Content.ReadAsStringAsync(cancellationToken)
        );
        Assert.Equal(HttpStatusCode.Created, repeat.StatusCode);
        Assert.Equal(first.Headers.Location, repeat.Headers.Location);
        Assert.Equal(
            $$"""{"id":"{{product}}"}""",
            await repeat.Content.ReadAsStringAsync(cancellationToken)
        );

        var read = await client.GetFromJsonAsync<JsonElement>(
            first.Headers.Location,
            cancellationToken
        );
        Assert.Equal("Desk lamp", read.GetProperty("name").GetString());
        Assert.Equal(category, read.GetProperty("categoryId").GetGuid());

        // One product in the category: the repeat created none.
        var page = await client.GetFromJsonAsync<JsonElement>(
            $"/catalog/products?size=10&sort=name&filter[category][eq]={category}",
            cancellationToken
        );
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        Assert.Equal(
            product,
            Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid()
        );
    }

    // The service defaults map /alive in every environment, so the Host is started as it is deployed, in Production,
    // beside the fixture's, and asked without a token, as an orchestrator's probe asks. /health is for developers: no
    // endpoint serves it there, so the Host's authenticated-by-default policy answers it. That liveness asks nothing
    // outside the process, so it stays healthy while the database is down, is the recipe's own proof (HostRecipeTests in
    // the Kit's Proving Sample, #52).
    [Fact]
    public async Task The_host_answers_alive_through_the_service_defaults()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var deployed = App.ComposeHost(
            Environments.Production,
            Fixture.ConnectionString
        );
        await HostFixture.StartHostAsync(deployed, cancellationToken);
        using var client = deployed.GetTestClient();

        var alive = await client.GetAsync("/alive", cancellationToken);
        var health = await client.GetAsync("/health", cancellationToken);
        await deployed.StopAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
        Assert.Equal("Healthy", await alive.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(HttpStatusCode.Unauthorized, health.StatusCode);
    }

    // Catalog's RemoveProducts declares a permission; without its check registered the Host never builds.
    [Fact]
    public void The_host_does_not_build_when_a_declared_permission_has_no_registered_check()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            App.ComposeHost(
                Environments.Development,
                Fixture.ConnectionString,
                builder =>
                {
                    foreach (
                        var check in builder
                            .Services.Where(descriptor =>
                                !descriptor.IsKeyedService
                                && descriptor.ServiceType.IsGenericType
                                && descriptor.ServiceType.GetGenericTypeDefinition()
                                    == typeof(IAuthorizer<>)
                                && descriptor.ImplementationType?.Assembly
                                    == typeof(RequiresPermissionAttribute).Assembly
                            )
                            .ToList()
                    )
                    {
                        builder.Services.Remove(check);
                    }
                }
            )
        );

        Assert.Contains(
            "RemoveProductsCommand declares RequiresPermissionAttribute, but its check is not registered",
            thrown.Message
        );
    }

    // With ConnectionStrings:broker the Host joins RabbitMQ under its service name, its queue on the broker, which it
    // checks as it is composed. The template derives that name from the project's name, lower-cased with a hyphen for each
    // run of other characters, so that every name gives a valid one, and writes it here and in AppHost.cs. The Host is
    // built and never started, so nothing reaches the broker; resolving Wolverine's options applies the Kit's extensions
    // to them, the RabbitMQ one setting the service name.
    [Fact]
    public async Task The_host_joins_a_configured_broker_under_its_derived_service_name()
    {
        await using var withBroker = App.ComposeHost(
            Environments.Development,
            Fixture.ConnectionString,
            null,
            "--ConnectionStrings:broker=amqp://guest:guest@localhost:5672/"
        );

        var options = withBroker.Services.GetRequiredService<WolverineOptions>();

        Assert.Equal("modular-monolith", options.ServiceName);
    }
}

/// <summary>
/// The application, as <see cref="AppHost"/> composes it for Program, served in memory over the PostgreSQL in a container
/// that <see cref="HostFixture"/> runs, migrates and resets, trusting the bearer tokens of a test issuer through the
/// configuration section Authentication:Schemes:Bearer, as any issuer is configured.
/// </summary>
public sealed class App : HostFixture
{
    private const string Issuer = "https://issuer.test";
    private const string Audience = "app";
    private static readonly SymmetricSecurityKey SigningKey = new(
        RandomNumberGenerator.GetBytes(32)
    );

    protected override IHost CreateHost(string connectionString) =>
        ComposeHost(Environments.Development, connectionString);

    /// <summary>
    /// The application in <paramref name="environment"/> on <paramref name="connectionString"/>, served in memory, built
    /// but not started; <paramref name="configure"/> may change its composition first, and <paramref name="arguments"/> add
    /// to the configuration it is composed from.
    /// </summary>
    public static WebApplication ComposeHost(
        string environment,
        string connectionString,
        Action<WebApplicationBuilder>? configure = null,
        params string[] arguments
    )
    {
        var builder = AppHost.CreateBuilder([
            "--environment",
            environment,
            $"--ConnectionStrings:app={connectionString}",
            $"--Authentication:Schemes:Bearer:ValidIssuer={Issuer}",
            $"--Authentication:Schemes:Bearer:ValidAudiences:0={Audience}",
            $"--Authentication:Schemes:Bearer:SigningKeys:0:Issuer={Issuer}",
            $"--Authentication:Schemes:Bearer:SigningKeys:0:Value={Convert.ToBase64String(SigningKey.Key)}",
            .. arguments,
        ]);
        builder.WebHost.UseTestServer();
        configure?.Invoke(builder);
        return AppHost.Build(builder);
    }

    /// <summary>The tenant every token names: Catalog is tenant-scoped, so each of its Operations needs one.</summary>
    public const string Tenant = "tests";

    /// <summary>
    /// A client that calls API version 2.0, with a bearer token for <paramref name="person"/> in <see cref="Tenant"/> when
    /// one is named.
    /// </summary>
    public HttpClient ClientAs(string? person = null)
    {
        var client = Host.GetTestClient();
        client.DefaultRequestHeaders.Add("api-version", "2.0");
        if (person is not null)
        {
            var token = new JsonWebTokenHandler().CreateToken(
                new SecurityTokenDescriptor
                {
                    Issuer = Issuer,
                    Audience = Audience,
                    Claims = new Dictionary<string, object>
                    {
                        ["sub"] = person,
                        ["tenant"] = Tenant,
                    },
                    Expires = DateTime.UtcNow.AddMinutes(5),
                    SigningCredentials = new SigningCredentials(
                        SigningKey,
                        SecurityAlgorithms.HmacSha256
                    ),
                }
            );
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                token
            );
        }

        return client;
    }

    /// <summary>POSTs <paramref name="value"/> as JSON with the <c>Idempotency-Key</c> <paramref name="key"/>.</summary>
    public static async Task<HttpResponseMessage> PostAsync<T>(
        HttpClient client,
        string uri,
        T value,
        string key,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(value),
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request, cancellationToken);
    }
}
