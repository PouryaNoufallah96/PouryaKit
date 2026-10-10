using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Contracts.Products.Events.V1;
using Catalog.Products.Features.CreateProduct;
using Inventory;
using Inventory.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;
using PouryaKit.Testing;
using RabbitMQ.Client;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Wolverine;
using Wolverine.Runtime;

namespace ModularMonolith.Tests.Fixtures;

/// <summary>
/// A RabbitMQ broker and a PostgreSQL server of their own, for the tests of delivery between Hosts: each test gets a
/// vhost and two databases, so its Hosts share nothing but the broker.
/// </summary>
public sealed class BrokerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.6")
        // Each pair of Hosts opens pools of its own.
        .WithCommand("-c", "track_commit_timestamp=on", "-c", "max_connections=1000")
        .Build();

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4.3.6").Build();

    public async ValueTask InitializeAsync() => await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());

    public async ValueTask DisposeAsync()
    {
        await _rabbit.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    /// <summary>A vhost and the Catalog Host's and Inventory Host's databases, new for one test.</summary>
    public async Task<BrokerEnvironment> CreateEnvironmentAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var vhost = $"t{suffix}";
        await RabbitCtlAsync("add_vhost", vhost);
        var broker = new UriBuilder(_rabbit.GetConnectionString()) { Path = "/" + vhost };
        await RabbitCtlAsync("set_permissions", "-p", vhost, broker.UserName, ".*", ".*", ".*");
        return new BrokerEnvironment(
            this,
            await CreateDatabaseAsync($"catalog_{suffix}"),
            await CreateDatabaseAsync($"inventory_{suffix}"),
            broker.Uri
        );
    }

    /// <summary>A new, empty database on the server.</summary>
    public async Task<string> CreateDatabaseAsync(string name)
    {
        await using var dataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());
        await using var create = dataSource.CreateCommand($"create database {name}");
        await create.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = name }.ConnectionString;
    }

    /// <summary>
    /// Closes every client connection to the vhost of <paramref name="broker"/> from the broker's side, as a broker
    /// restart or a network cut does, while the Hosts on it keep running.
    /// </summary>
    public Task DropConnectionsAsync(Uri broker) => RabbitCtlAsync("close_all_connections", "-p", broker.AbsolutePath.TrimStart('/'), "forced drop");

    private async Task RabbitCtlAsync(params string[] arguments)
    {
        var result = await _rabbit.ExecAsync(["rabbitmqctl", .. arguments]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"rabbitmqctl {string.Join(' ', arguments)}: {result.Stderr}");
        }
    }
}

/// <summary>
/// One test's broker vhost and databases, and the two Hosts of the Proving Sample's service composition on them:
/// <c>ModularMonolith.Host</c> with a broker, which hosts Catalog and Billing, and <c>Inventory.Host</c>.
/// </summary>
public sealed class BrokerEnvironment(BrokerFixture fixture, string catalog, string inventory, Uri broker)
{
    /// <summary>The tests' own second subscription of Inventory.Host to <see cref="ProductCreatedV1"/>.</summary>
    public const string AuditSubscription = "inventory.audit-product";

    /// <summary>The Catalog Host's database.</summary>
    public string Catalog => catalog;

    /// <summary>The Inventory Host's database.</summary>
    public string Inventory => inventory;

    /// <summary>The AMQP address of the test's vhost.</summary>
    public Uri Broker => broker;

    public RecordedLogs Logs { get; } = new();

    /// <summary>Starts <c>ModularMonolith.Host</c> on <see cref="Catalog"/> with the broker, as the tests compose it.</summary>
    public async Task<WebApplication> StartCatalogHostAsync(Action<WebApplicationBuilder>? configure = null)
    {
        var host = AppFixture.ComposeHost("Development", catalog, Logs, configure, $"--ConnectionStrings:broker={broker}");
        await HostFixture.StartHostAsync(host, TestContext.Current.CancellationToken);
        return host;
    }

    /// <summary>
    /// Builds <c>Inventory.Host</c> on <paramref name="database"/>, <see cref="Inventory"/> unless given, with the
    /// test's delivery speed, <see cref="DeliveryProbes"/> and <see cref="AuditSubscription"/>, served in memory; not
    /// started.
    /// </summary>
    public IHost ComposeInventoryHost(
        string serviceName = InventoryHost.ServiceName,
        string? database = null,
        Action<WebApplicationBuilder>? configure = null
    )
    {
        var builder = InventoryHost.CreateBuilder(
            [
                "--environment",
                "Development",
                $"--ConnectionStrings:app={database ?? inventory}",
                $"--ConnectionStrings:{InventoryHost.BrokerConnection}={broker}",
                "--Inventory:ReconcileStock:Every=01:00:00",
            ],
            serviceName
        );
        builder.Logging.ClearProviders().AddProvider(Logs);
        builder.Services.UsePouryaKitTestDelivery();
        DeliveryProbes.Install(builder.Services);
        builder.Services.Subscribe<ProductCreatedV1, AuditProductIntegrationEventHandler>(AuditSubscription, typeof(InventoryModule));
        builder.WebHost.UseTestServer();
        configure?.Invoke(builder);
        return InventoryHost.Build(builder);
    }

    /// <summary>
    /// Starts <see cref="ComposeInventoryHost"/>'s Host, after it applies Inventory's migrations, with the table of
    /// <see cref="AuditSubscription"/>'s effect.
    /// </summary>
    public async Task<IHost> StartInventoryHostAsync(
        string serviceName = InventoryHost.ServiceName,
        string? database = null,
        Action<WebApplicationBuilder>? configure = null
    )
    {
        await Sql.RowsAsync(
            database ?? inventory,
            "create schema if not exists tests; create table if not exists tests.product_audits (product_id uuid not null)"
        );
        var host = ComposeInventoryHost(serviceName, database, configure);
        await HostFixture.StartHostAsync(host, TestContext.Current.CancellationToken);
        return host;
    }

    /// <summary>A new database for another Inventory Host on the same broker.</summary>
    public Task<string> CreateDatabaseAsync(string name) => fixture.CreateDatabaseAsync($"{name}_{Guid.NewGuid().ToString("N")[..10]}");

    /// <summary>Creates a product in a category of its own through Catalog's Gateway, the product as <paramref name="context"/>.</summary>
    public static async Task CreateProductAsync(IHost catalogHost, Guid product, string name, OperationContext? context = null)
    {
        var gateway = catalogHost.Services.GetRequiredService<IGatewayProcessor<CatalogModule>>();
        var category = Guid.CreateVersion7();
        Assert.True((await gateway.SendAsync(new CreateCategoryCommand(category, $"Lamps {category}"), TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True(
            (
                await gateway.SendAsync(
                    new CreateProductCommand(product, category, name),
                    context ?? new OperationContext(GatewayTestExtensions.TestActor),
                    TestContext.Current.CancellationToken
                )
            ).IsSuccess
        );
    }

    /// <summary>
    /// The identity of the Domain Event every Integration Event about <paramref name="product"/> carries, once Billing,
    /// beside Catalog, has applied its ProductCreatedV1 with a Receipt of it.
    /// </summary>
    public async Task<Guid> CreatedEventOfAsync(Guid product)
    {
        var applied =
            $"from billing.price_lists p join billing.pouryakit_receipts r on r.subscription = 'billing.product-created' and pg_xact_commit_timestamp(r.xmin) = pg_xact_commit_timestamp(p.xmin) where p.product_id = '{product}'";
        await WaitUntilAsync(catalog, $"exists (select {applied})", "Billing never applied the product's ProductCreatedV1.");
        return await ScalarAsync<Guid>(catalog, $"select r.message_id {applied}");
    }

    /// <summary>Waits until the SQL <paramref name="condition"/> holds on <paramref name="database"/>; fails with <paramref name="because"/>.</summary>
    public static async Task WaitUntilAsync(string database, string condition, string because, int seconds = 30)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        while (!await ScalarAsync<bool>(database, $"select {condition}"))
        {
            Assert.False(timeout.IsCancellationRequested, because);
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Waits until no delivery in the message store of <paramref name="database"/> is pending, as
    /// <see cref="HostFixture"/> defines one: every envelope handled, dead-lettered or a delayed command not yet due,
    /// nothing waiting to be sent or replayed.
    /// </summary>
    public static Task WaitUntilSettledAsync(string database) =>
        new StoredDeliveries(database, "wolverine", []).WaitUntilSettledAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    public static async Task<T> ScalarAsync<T>(string database, string sql) => (T)(await Sql.RowsAsync(database, sql))[0][0];

    /// <summary>Messages ready in the broker queue <paramref name="queue"/>, read with a passive declare.</summary>
    public async Task<uint> BrokerMessagesAsync(string queue)
    {
        await using var connection = await new ConnectionFactory { Uri = broker }.CreateConnectionAsync(TestContext.Current.CancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
        return await channel.MessageCountAsync(queue, TestContext.Current.CancellationToken);
    }

    /// <summary>Binds the broker queue <paramref name="queue"/> to the exchange <paramref name="exchange"/>, as by hand.</summary>
    public async Task BindAsync(string queue, string exchange)
    {
        await using var connection = await new ConnectionFactory { Uri = broker }.CreateConnectionAsync(TestContext.Current.CancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
        await channel.QueueBindAsync(queue, exchange, "", cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>Consumers of the broker queue <paramref name="queue"/>, none while it does not exist.</summary>
    public async Task<uint> BrokerConsumersAsync(string queue)
    {
        await using var connection = await new ConnectionFactory { Uri = broker }.CreateConnectionAsync(TestContext.Current.CancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            return (await channel.QueueDeclarePassiveAsync(queue, TestContext.Current.CancellationToken)).ConsumerCount;
        }
        catch (RabbitMQ.Client.Exceptions.OperationInterruptedException)
        {
            return 0;
        }
    }

    /// <summary>Applies Inventory's migrations to <see cref="Inventory"/>, as a deployment does before the Host starts.</summary>
    public async Task MigrateInventoryAsync()
    {
        using var host = ComposeInventoryHost();
        await using var scope = host.Services.CreateAsyncScope();
        foreach (var options in scope.ServiceProvider.GetServices<DbContextOptions>())
        {
            await ((DbContext)scope.ServiceProvider.GetRequiredService(options.ContextType)).Database.MigrateAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Stops <paramref name="host"/> as a killed process leaves it: no drain, no release of what it owns.</summary>
    public static async Task KillAsync(IHost host)
    {
        ((WolverineRuntime)host.Services.GetRequiredService<IWolverineRuntime>()).StopMode = StopMode.Quick;
        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();
    }
}
