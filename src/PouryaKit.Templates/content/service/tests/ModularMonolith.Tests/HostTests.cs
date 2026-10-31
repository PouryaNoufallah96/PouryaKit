using System.Net;
using System.Net.Http.Headers;
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
using Npgsql;
using PouryaKit.Testing;
using Testcontainers.RabbitMq;
using Wolverine;

[assembly: AssemblyFixture(typeof(App))]
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

namespace ModularMonolith.Tests;

// The Host as Program composes it, over PostgreSQL and the RabbitMQ broker it joins: Inventory's read over HTTP through its
// Gateway, Dapper and PostgreSQL, with a bearer token the Host verifies; its liveness; and its queue on the broker.
public sealed class HostTests(App app) : HostTest<App>(app)
{
    // Catalog runs in another Host, so the stock item is written as Inventory's delivery of ProductCreatedV1 writes it, for
    // the tenant the delivery carried.
    [Fact]
    public async Task A_stock_item_is_read_under_api_version_2_by_an_actor_of_its_tenant_and_by_no_anonymous_caller()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var product = Guid.CreateVersion7();
        await using (var connection = new NpgsqlConnection(Fixture.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var open = new NpgsqlCommand(
                """
                insert into inventory.stock_items
                    (id, product_id, tenant, quantity_on_hand, opened_on, created_at, created_by, last_changed_at, last_changed_by, version)
                values ($1, $2, $3, 0, now(), now(), 'tests', now(), 'tests', 1)
                """,
                connection
            )
            {
                Parameters =
                {
                    new() { Value = Guid.CreateVersion7() },
                    new() { Value = product },
                    new() { Value = App.Tenant },
                },
            };
            await open.ExecuteNonQueryAsync(cancellationToken);
        }

        using var reader = Fixture.ClientAs("ada");
        using var anonymous = Fixture.ClientAs();

        var read = await reader.GetAsync($"/inventory/v2/stock-items/{product}", cancellationToken);
        var refused = await anonymous.GetAsync(
            $"/inventory/v2/stock-items/{product}",
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(
            $$"""{"productId":"{{product}}","quantityOnHand":0}""",
            await read.Content.ReadAsStringAsync(cancellationToken)
        );
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    // The service defaults map /alive in every environment, so the Host is started as it is deployed, in Production,
    // beside the fixture's, and asked without a token, as an orchestrator's probe asks. /health is for developers: no
    // endpoint serves it there, so the Host's authenticated-by-default policy answers it.
    [Fact]
    public async Task The_host_answers_alive_through_the_service_defaults()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var deployed = Fixture.ComposeHost(
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

    // The started Host consumes from its queue on the broker, named by its service name, which the template derives from
    // the project's name, lower-cased with a hyphen for each run of other characters, and writes here and in AppHost.cs.
    // The queue is bound to the exchange of each wire name the Host receives, so what Catalog's Host publishes reaches it.
    [Fact]
    public async Task The_host_receives_catalogs_wire_names_on_its_queue_under_its_derived_service_name()
    {
        var options = Fixture.Host.Services.GetRequiredService<WolverineOptions>();

        var queues = await Fixture.BrokerListAsync("list_queues", "name", "consumers");
        var bindings = await Fixture.BrokerListAsync(
            "list_bindings",
            "source_name",
            "destination_name"
        );

        Assert.Equal("modular-monolith", options.ServiceName);
        Assert.Contains(
            queues,
            queue =>
                queue.GetProperty("name").GetString() == "modular-monolith"
                && queue.GetProperty("consumers").GetInt32() > 0
        );
        Assert.Equal(
            [
                "catalog.product-created.v1",
                "catalog.product-created.v2",
                "catalog.product-snapshot.v1",
            ],
            bindings
                .Where(binding =>
                    binding.GetProperty("destination_name").GetString() == "modular-monolith"
                )
                .Select(binding => binding.GetProperty("source_name").GetString())
                .Where(exchange => exchange is not "")
                .Order(StringComparer.Ordinal)
        );
    }
}

/// <summary>
/// The application, as <see cref="AppHost"/> composes it for Program, served in memory over the PostgreSQL in a container
/// that <see cref="HostFixture"/> runs, migrates and resets, and the RabbitMQ in a container started before it, trusting the
/// bearer tokens of a test issuer through the configuration section Authentication:Schemes:Bearer, as any issuer is
/// configured.
/// </summary>
public sealed class App : HostFixture, IAsyncLifetime
{
    private const string Issuer = "https://issuer.test";
    private const string Audience = "app";
    private static readonly SymmetricSecurityKey SigningKey = new(
        RandomNumberGenerator.GetBytes(32)
    );

    private readonly RabbitMqContainer _broker = new RabbitMqBuilder("rabbitmq:4.3.6").Build();

    // The Host joins the broker as it is composed, so the broker starts first; then HostFixture starts PostgreSQL and the Host.
    async ValueTask IAsyncLifetime.InitializeAsync()
    {
        await _broker.StartAsync(TestContext.Current.CancellationToken);
        await InitializeAsync();
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        try
        {
            await DisposeAsync();
        }
        finally
        {
            await _broker.DisposeAsync();
        }
    }

    protected override IHost CreateHost(string connectionString) =>
        ComposeHost(Environments.Development, connectionString);

    /// <summary>
    /// The application in <paramref name="environment"/> on <paramref name="connectionString"/> and the fixture's broker,
    /// served in memory, built but not started.
    /// </summary>
    public WebApplication ComposeHost(string environment, string connectionString)
    {
        var builder = AppHost.CreateBuilder([
            "--environment",
            environment,
            $"--ConnectionStrings:app={connectionString}",
            $"--ConnectionStrings:broker={_broker.GetConnectionString()}",
            $"--Authentication:Schemes:Bearer:ValidIssuer={Issuer}",
            $"--Authentication:Schemes:Bearer:ValidAudiences:0={Audience}",
            $"--Authentication:Schemes:Bearer:SigningKeys:0:Issuer={Issuer}",
            $"--Authentication:Schemes:Bearer:SigningKeys:0:Value={Convert.ToBase64String(SigningKey.Key)}",
        ]);
        builder.WebHost.UseTestServer();
        return AppHost.Build(builder);
    }

    /// <summary>The tenant every token names, whose stock items its caller reads.</summary>
    public const string Tenant = "tests";

    /// <summary>A client with a bearer token for <paramref name="person"/> in <see cref="Tenant"/> when one is named.</summary>
    public HttpClient ClientAs(string? person = null)
    {
        var client = Host.GetTestClient();
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

    /// <summary>The rows <c>rabbitmqctl</c> lists of <paramref name="items"/> on the broker, each with <paramref name="columns"/>.</summary>
    public async Task<JsonElement[]> BrokerListAsync(string items, params string[] columns)
    {
        var listed = await _broker.ExecAsync(
            ["rabbitmqctl", "--quiet", items, .. columns, "--formatter", "json"],
            TestContext.Current.CancellationToken
        );
        Assert.Equal(0, listed.ExitCode);
        return JsonSerializer.Deserialize<JsonElement[]>(listed.Stdout)!;
    }
}
