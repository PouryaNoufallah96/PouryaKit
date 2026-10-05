using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using PouryaKit.Modularity;
using Respawn;
using Respawn.Graph;
using Testcontainers.PostgreSql;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace PouryaKit.Testing;

/// <summary>
/// Hosts the application against a PostgreSQL started in a container: it builds the Host with
/// <see cref="CreateHost"/>, applies every Module's migrations, and then starts it, so a hosted service such as
/// Wolverine finds every Module's tables, Receipts included, when the Host starts. Share one per test assembly and derive
/// test classes from <see cref="HostTest{TFixture}"/>, which resets the data before each test. The server records
/// commit timestamps, so a test can tell whether two rows were committed together.
/// </summary>
public abstract class HostFixture : IAsyncLifetime
{
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(30);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.6")
        .WithCommand("-c", "track_commit_timestamp=on")
        .Build();

    private IHost? _host;
    private Respawner? _respawner;
    private StoredDeliveries? _deliveries;

    /// <summary>The connection string of the test database.</summary>
    public string ConnectionString => _postgres.GetConnectionString();

    /// <summary>The started Host.</summary>
    public IHost Host => _host ?? throw new InvalidOperationException("The Host starts in InitializeAsync.");

    /// <summary>
    /// Builds the application's Host, not yet started, with <paramref name="connectionString"/> as the connection
    /// string its Modules use. A Host that delivers Notifications calls <c>UsePouryaKitTestDelivery</c> on its services.
    /// </summary>
    protected abstract IHost CreateHost(string connectionString);

    /// <summary>The Gateway of <typeparamref name="TModule"/>, for sending Operations as the application does.</summary>
    public IGatewayProcessor<TModule> Gateway<TModule>()
        where TModule : IModuleDefinition => Host.Services.GetRequiredService<IGatewayProcessor<TModule>>();

    /// <summary>
    /// Waits until every delivery an earlier test caused has finished, then deletes every row except the migrations
    /// history, in every schema, and every stored delivery.
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        var deliveries = Deliveries();
        if (deliveries is not null)
        {
            await deliveries.WaitUntilSettledAsync(DeliveryTimeout, cancellationToken);
        }

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        // The message store's own tables, such as its node records, belong to the running Host; only its deliveries go.
        _respawner ??= await Respawner.CreateAsync(
            connection,
            new RespawnerOptions
            {
                DbAdapter = DbAdapter.Postgres,
                TablesToIgnore = [new Table(HistoryRepository.DefaultTableName)],
                SchemasToExclude = deliveries is null ? [] : [deliveries.Schema],
            }
        );
        await _respawner.ResetAsync(connection);
        if (deliveries is not null)
        {
            await deliveries.DeleteAllAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Runs <paramref name="operation"/>, typically a Gateway call, then waits until every delivery it caused has been
    /// handled or dead-lettered, including deliveries those deliveries caused, and returns them in the order stored.
    /// Fails when the Operation stored no delivery at all, so a test that waits for deliveries never passes on none.
    /// </summary>
    public async Task<(T Outcome, IReadOnlyList<Delivery> Deliveries)> WaitForDeliveriesAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        var store = Deliveries() ?? throw new InvalidOperationException("The Host stores no deliveries.");
        var before = await store.IdsAsync(cancellationToken);

        var outcome = await operation();

        if ((await store.IdsAsync(cancellationToken)).IsSubsetOf(before))
        {
            throw new DeliveryAssertionException(
                "The Operation sent nothing: its commit stored no delivery, so there is nothing to wait for."
            );
        }

        await store.WaitUntilSettledAsync(DeliveryTimeout, cancellationToken);
        return (outcome, [.. (await store.AllAsync(cancellationToken)).Where(row => !before.Contains(row.Id)).Select(row => row.Delivery)]);
    }

    /// <summary>
    /// Waits until no stored delivery is pending: every one handled or dead-lettered, such as after a test handed one
    /// out again.
    /// </summary>
    public Task WaitUntilDeliveredAsync(CancellationToken cancellationToken = default) =>
        (Deliveries() ?? throw new InvalidOperationException("The Host stores no deliveries.")).WaitUntilSettledAsync(
            DeliveryTimeout,
            cancellationToken
        );

    /// <summary>
    /// Runs <paramref name="operation"/> and fails unless it stored no delivery: what a rolled-back Operation leaves.
    /// A delivery is stored in the Operation's own commit, so nothing needs waiting for.
    /// </summary>
    public async Task<T> AssertNothingSentAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var store = Deliveries() ?? throw new InvalidOperationException("The Host stores no deliveries.");
        var before = await store.IdsAsync(cancellationToken);

        var outcome = await operation();

        var sent = (await store.AllAsync(cancellationToken)).Where(row => !before.Contains(row.Id)).ToList();
        if (sent.Count > 0)
        {
            throw new DeliveryAssertionException(
                $"The Operation sent {sent.Count} deliveries: {string.Join(", ", sent.Select(row => row.Delivery.Subscription))}."
            );
        }

        return outcome;
    }

    /// <summary>
    /// Applies every Module's migrations from a scope of <paramref name="host"/>, then starts it: what
    /// <see cref="InitializeAsync"/> does with the fixture's Host, for a test that starts another Host of the application.
    /// </summary>
    public static async Task StartHostAsync(IHost host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        // Every DbContext registered with AddDbContext is also registered as one non-generic DbContextOptions.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            foreach (var options in scope.ServiceProvider.GetServices<DbContextOptions>())
            {
                var dbContext = (DbContext)scope.ServiceProvider.GetRequiredService(options.ContextType);
                await dbContext.Database.MigrateAsync(cancellationToken);
            }
        }

        await host.StartAsync(cancellationToken);
    }

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        _host = CreateHost(ConnectionString);
        await StartHostAsync(_host);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        await _postgres.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private StoredDeliveries? Deliveries() =>
        _deliveries ??= Host.Services.GetService<IWolverineRuntime>()?.Storage is IMessageDatabase store
            ? new StoredDeliveries(ConnectionString, store.SchemaName)
            : null;
}
