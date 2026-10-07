using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using JasperFx.CommandLine;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ModularMonolith.Tests;
using PouryaKit.Cqrs;
using PouryaKit.Security;
using Testcontainers.PostgreSql;
using Wolverine;

[assembly: AssemblyFixture(typeof(App))]

namespace ModularMonolith.Tests;

// The Host over HTTP, as Program composes it: Catalog's Features through the pipeline, the Unit of Work and PostgreSQL,
// with a bearer token the Host verifies, and the composition's start checks.
public sealed class HostTests(App app)
{
    [Fact]
    public async Task A_product_created_with_an_idempotency_key_is_read_and_listed_and_its_repeat_creates_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = app.ClientAs("ada");
        var category = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        var body = new { productId = product, categoryId = category, name = "Desk lamp" };
        var key = Guid.CreateVersion7().ToString();

        var lamps = await client.PostAsJsonAsync("/catalog/categories", new { categoryId = category, name = "Lamps" }, cancellationToken);
        var first = await App.PostAsync(client, "/catalog/products", body, key, cancellationToken);
        var repeat = await App.PostAsync(client, "/catalog/products", body, key, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, lamps.StatusCode);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal($"/catalog/v2/products/{product}", first.Headers.Location?.OriginalString);
        Assert.Equal($$"""{"id":"{{product}}"}""", await first.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(HttpStatusCode.Created, repeat.StatusCode);
        Assert.Equal(first.Headers.Location, repeat.Headers.Location);
        Assert.Equal($$"""{"id":"{{product}}"}""", await repeat.Content.ReadAsStringAsync(cancellationToken));

        var read = await client.GetFromJsonAsync<JsonElement>(first.Headers.Location, cancellationToken);
        Assert.Equal("Desk lamp", read.GetProperty("name").GetString());
        Assert.Equal(category, read.GetProperty("categoryId").GetGuid());

        // The list is the storefront's, open to anonymous callers. One product in the category: the repeat created none.
        using var storefront = app.ClientAs();
        var page = await storefront.GetFromJsonAsync<JsonElement>(
            $"/catalog/products?size=10&sort=name&filter[category][eq]={category}",
            cancellationToken
        );
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        Assert.Equal(product, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    // The service defaults' liveness endpoint is mapped. That it asks nothing outside the process, so it stays healthy while
    // the database is down, is the recipe's own proof (HostRecipeTests in the Kit's Proving Sample, #52).
    [Fact]
    public async Task The_host_answers_alive_through_the_service_defaults()
    {
        using var client = app.ClientAs();

        var alive = await client.GetAsync("/alive", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
        Assert.Equal("Healthy", await alive.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    // Catalog's RemoveProducts declares a permission; without its check registered the Host stops when it starts.
    [Fact]
    public void The_host_does_not_start_when_a_declared_permission_has_no_registered_check()
    {
        using var withoutChecks = app.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                foreach (
                    var check in services
                        .Where(descriptor =>
                            !descriptor.IsKeyedService
                            && descriptor.ServiceType.IsGenericType
                            && descriptor.ServiceType.GetGenericTypeDefinition() == typeof(IAuthorizer<>)
                            && descriptor.ImplementationType?.Assembly == typeof(RequiresPermissionAttribute).Assembly
                        )
                        .ToList()
                )
                {
                    services.Remove(check);
                }
            })
        );

        var thrown = Assert.Throws<InvalidOperationException>(() => withoutChecks.Services);

        Assert.Contains("RemoveProductsCommand declares RequiresPermissionAttribute, but its check is not registered", thrown.Message);
    }

    // With ConnectionStrings:broker the Host joins RabbitMQ under its service name, its queue on the broker, which it
    // checks as it is composed. The template derives that name from the project's name, lower-cased with a hyphen for each
    // run of other characters, so that every name gives a valid one, and writes it here and in Program.cs. No hosted
    // service runs, Wolverine's among them, so nothing reaches the broker; Wolverine applies the Kit's extensions to its
    // options only when it starts, so the test applies them.
    [Fact]
    public void The_host_joins_a_configured_broker_under_its_derived_service_name()
    {
        using var withBroker = app.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:broker", "amqp://guest:guest@localhost:5672/");
            builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        });

        var options = withBroker.Services.GetRequiredService<WolverineOptions>();
        foreach (var extension in withBroker.Services.GetServices<IWolverineExtension>())
        {
            extension.Configure(options);
        }

        Assert.Equal("modular-monolith", options.ServiceName);
    }
}

/// <summary>
/// The application, served in memory over PostgreSQL in a container, trusting the bearer tokens of a test issuer
/// through the configuration section Authentication:Schemes:Bearer, as any issuer is configured.
/// </summary>
public sealed class App : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string Issuer = "https://issuer.test";
    private const string Audience = "app";
    private static readonly SymmetricSecurityKey SigningKey = new(RandomNumberGenerator.GetBytes(32));
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.6").Build();

    public string ConnectionString => _postgres.GetConnectionString();

    // The Host starts once, before any test: tests of several classes run at the same time, and would each start one.
    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        // Program hands its command line to JasperFx, which reads the factory's settings as no command it knows; this makes
        // JasperFx start the Host first (Wolverine's integration testing guide).
        JasperFxEnvironment.AutoStartHost = true;
        _ = Services;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    /// <summary>A client that calls API version 2.0, with a bearer token for <paramref name="person"/> when one is named.</summary>
    public HttpClient ClientAs(string? person = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("api-version", "2.0");
        if (person is not null)
        {
            var token = new JsonWebTokenHandler().CreateToken(
                new SecurityTokenDescriptor
                {
                    Issuer = Issuer,
                    Audience = Audience,
                    Claims = new Dictionary<string, object> { ["sub"] = person },
                    Expires = DateTime.UtcNow.AddMinutes(5),
                    SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
                }
            );
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    /// <summary>POSTs <paramref name="value"/> as JSON with the <c>Idempotency-Key</c> <paramref name="key"/>.</summary>
    public static async Task<HttpResponseMessage> PostAsync<T>(HttpClient client, string uri, T value, string key, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(value) };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request, cancellationToken);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:app", ConnectionString);
        builder.UseSetting("Authentication:Schemes:Bearer:ValidIssuer", Issuer);
        builder.UseSetting("Authentication:Schemes:Bearer:ValidAudiences:0", Audience);
        builder.UseSetting("Authentication:Schemes:Bearer:SigningKeys:0:Issuer", Issuer);
        builder.UseSetting("Authentication:Schemes:Bearer:SigningKeys:0:Value", Convert.ToBase64String(SigningKey.Key));
        // The first hosted service, so the Host starts it, and waits for it, before Wolverine. Program starts the Host
        // itself as soon as it is built, so a migration after the build would run beside the start.
        builder.ConfigureTestServices(services => services.Insert(0, ServiceDescriptor.Singleton<IHostedService, Migrations>()));
    }

    // Applies each Module's migrations: a Module's DbContext is internal to it, so each is found through the
    // DbContextOptions its registration adds.
    private sealed class Migrations(IServiceScopeFactory scopes) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await using var scope = scopes.CreateAsyncScope();
            foreach (var options in scope.ServiceProvider.GetServices<DbContextOptions>())
            {
                await ((DbContext)scope.ServiceProvider.GetRequiredService(options.ContextType)).Database.MigrateAsync(cancellationToken);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
