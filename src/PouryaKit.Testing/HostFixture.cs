using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using PouryaKit.Modularity;
using Respawn;
using Respawn.Graph;
using Testcontainers.PostgreSql;
using Weasel.Core.Migrations;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
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
    public IHost Host =>
        _host ?? throw new InvalidOperationException("The Host starts in InitializeAsync.");

    /// <summary>
    /// Builds the application's Host, not yet started, with <paramref name="connectionString"/> as the connection
    /// string its Modules use. A Host that delivers Notifications calls <c>UsePouryaKitTestDelivery</c> on its services.
    /// </summary>
    protected abstract IHost CreateHost(string connectionString);

    /// <summary>The Gateway of <typeparamref name="TModule"/>, for sending Operations as the application does.</summary>
    public IGatewayProcessor<TModule> Gateway<TModule>()
        where TModule : IModuleDefinition =>
        Host.Services.GetRequiredService<IGatewayProcessor<TModule>>();

    /// <summary>
    /// Waits until every delivery an earlier test caused has finished, then deletes every row except the migrations
    /// history and the message store's own tables, in every schema, and every stored delivery, delayed commands not yet
    /// due included. A Module whose tables share the store's schema is reset like any other.
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
        // They are spared by name, so the store may share its schema with a Module whose tables are reset.
        _respawner ??= await Respawner.CreateAsync(
            connection,
            new RespawnerOptions
            {
                DbAdapter = DbAdapter.Postgres,
                TablesToIgnore =
                [
                    new Table(HistoryRepository.DefaultTableName),
                    .. (deliveries?.StoreTables ?? []).Select(table => new Table(
                        table.Schema,
                        table.Name
                    )),
                ],
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
    /// handled or dead-lettered, including deliveries those deliveries caused, and returns them in no particular order. A
    /// delayed command not yet due is returned without waiting for it; it is waited for once due.
    /// Fails when the Operation stored no delivery at all, so a test that waits for deliveries never passes on none.
    /// It first waits until no earlier delivery is pending, so nothing an earlier Operation caused is credited to this one.
    /// </summary>
    public async Task<(T Outcome, IReadOnlyList<Delivery> Deliveries)> WaitForDeliveriesAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        var store =
            Deliveries() ?? throw new InvalidOperationException("The Host stores no deliveries.");
        var before = await SettledIdsAsync(store, cancellationToken);

        var outcome = await operation();

        if ((await store.IdsAsync(cancellationToken)).IsSubsetOf(before))
        {
            throw new DeliveryAssertionException(
                "The Operation sent nothing: its commit stored no delivery, so there is nothing to wait for."
            );
        }

        await store.WaitUntilSettledAsync(DeliveryTimeout, cancellationToken);
        return (
            outcome,
            [
                .. (await store.AllAsync(cancellationToken))
                    .Where(row => !before.Contains(row.Id))
                    .Select(row => row.Delivery),
            ]
        );
    }

    /// <summary>
    /// Waits until no stored delivery is pending: every one handled, dead-lettered or a delayed command not yet due, such
    /// as after a test handed one out again.
    /// </summary>
    public Task WaitUntilDeliveredAsync(CancellationToken cancellationToken = default) =>
        (
            Deliveries() ?? throw new InvalidOperationException("The Host stores no deliveries.")
        ).WaitUntilSettledAsync(DeliveryTimeout, cancellationToken);

    /// <summary>
    /// Every delivery the message store has set aside as a dead letter, read through Wolverine's dead-letter storage as
    /// an application inspects it. Each test starts with none, so these are what its Operations caused.
    /// </summary>
    public async Task<IReadOnlyList<DeadLetter>> DeadLettersAsync(
        CancellationToken cancellationToken = default
    )
    {
        // A page size of 0 reads every one.
        var found = await DeadLetterStorage()
            .QueryAsync(new DeadLetterEnvelopeQuery { PageSize = 0 }, cancellationToken);
        return
        [
            .. found.Envelopes.Select(deadLetter => new DeadLetter(
                deadLetter.Id,
                StoredDeliveries.SubscriptionAt(deadLetter.ReceivedAt),
                deadLetter.MessageType,
                deadLetter.ExceptionType,
                deadLetter.ExceptionMessage
            )),
        ];
    }

    /// <summary>
    /// Replays <paramref name="deadLetter"/> through Wolverine, which moves it back to its subscription's queue, where only
    /// that subscription's handler runs, then waits until it is handled or set aside again. Fails when it is not a dead
    /// letter, so a replay never passes on nothing.
    /// </summary>
    public async Task ReplayAsync(
        DeadLetter deadLetter,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(deadLetter);
        var storage = DeadLetterStorage();
        if (await storage.DeadLetterEnvelopeByIdAsync(deadLetter.Id) is null)
        {
            throw new DeliveryAssertionException(
                $"The delivery {deadLetter.Id} to {deadLetter.Subscription} is not a dead letter, so there is nothing to replay."
            );
        }

        await storage.ReplayAsync(new DeadLetterEnvelopeQuery([deadLetter.Id]), cancellationToken);
        await WaitUntilDeliveredAsync(cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="operation"/> and fails unless it sent nothing: what a rolled-back Operation leaves. A delivery
    /// to a subscription of the Host is stored; an envelope for a broker is stored until it is sent, then deleted, so the
    /// assertion also counts every envelope Wolverine handed to a sending agent while the Operation ran, and every one still
    /// stored for one afterwards, such as while its broker is unavailable.
    /// It first waits until no earlier delivery is pending, so nothing an earlier Operation caused is charged to this
    /// one; an envelope is stored in the Operation's own commit and handed out before the Operation returns, so nothing
    /// needs waiting for afterwards.
    /// </summary>
    public async Task<T> AssertNothingSentAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        var store =
            Deliveries() ?? throw new InvalidOperationException("The Host stores no deliveries.");
        var before = await SettledIdsAsync(store, cancellationToken);
        using var handedOut = new SentEnvelopes(
            Host.Services.GetRequiredService<IWolverineRuntime>()
        );

        var outcome = await operation();

        string[] sent =
        [
            .. (await store.AllAsync(cancellationToken))
                .Where(row => !before.Contains(row.Id))
                .Select(row => row.Delivery.Subscription),
            .. handedOut.Envelopes.Concat(await store.OutgoingAsync(cancellationToken)).Distinct(),
        ];
        if (sent.Length > 0)
        {
            throw new DeliveryAssertionException($"The Operation sent {string.Join(", ", sent)}.");
        }

        return outcome;
    }

    /// <summary>
    /// Applies every Module's migrations from a scope of <paramref name="host"/>, then starts it: what
    /// <see cref="InitializeAsync"/> does with the fixture's Host, for a test that starts another Host of the application.
    /// </summary>
    public static async Task StartHostAsync(
        IHost host,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(host);
        // Every DbContext registered with AddDbContext is also registered as one non-generic DbContextOptions.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            foreach (var options in scope.ServiceProvider.GetServices<DbContextOptions>())
            {
                var dbContext = (DbContext)
                    scope.ServiceProvider.GetRequiredService(options.ContextType);
                await dbContext.Database.MigrateAsync(cancellationToken);
            }
        }

        await host.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync(TestContext.Current.CancellationToken);
        _host = CreateHost(ConnectionString);
        await StartHostAsync(_host, TestContext.Current.CancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The Host is disposed even when it fails to stop, and the database container whatever the Host does; a failure
    /// still reaches the caller.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_host is not null)
            {
                try
                {
                    await _host.StopAsync();
                }
                finally
                {
                    if (_host is IAsyncDisposable host)
                    {
                        await host.DisposeAsync();
                    }
                    else
                    {
                        _host.Dispose();
                    }
                }
            }
        }
        finally
        {
            await _postgres.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    // A delivery still running when the baseline is read could store one more before the Operation ends.
    private static async Task<HashSet<Guid>> SettledIdsAsync(
        StoredDeliveries store,
        CancellationToken cancellationToken
    )
    {
        await store.WaitUntilSettledAsync(DeliveryTimeout, cancellationToken);
        return await store.IdsAsync(cancellationToken);
    }

    // Every envelope the Host's Wolverine hands to a sending agent for another address than one of its local queues, as
    // Wolverine counts it in its wolverine-messages-sent instrument, on that runtime's own Meter, until disposed. Wolverine
    // counts an envelope when a sending agent takes it, before its broker has it, so the count stays when the store deletes
    // the sent envelope; a delivery to a local queue is stored, and counted there.
    private sealed class SentEnvelopes : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<string> _envelopes = [];

        public SentEnvelopes(IWolverineRuntime runtime)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (
                    instrument.Meter == runtime.Meter
                    && instrument.Name == MetricsConstants.MessagesSent
                )
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<int>(
                (_, _, tags, _) =>
                {
                    string? type = null,
                        destination = null;
                    foreach (var tag in tags)
                    {
                        (type, destination) = tag.Key switch
                        {
                            "message.type" => (tag.Value as string, destination),
                            "message.destination" => (type, tag.Value as string),
                            _ => (type, destination),
                        };
                    }

                    if (
                        destination is not null
                        && !destination.StartsWith("local:", StringComparison.Ordinal)
                    )
                    {
                        _envelopes.Enqueue($"{type} to {destination}");
                    }
                }
            );
            _listener.Start();
        }

        public IReadOnlyCollection<string> Envelopes => _envelopes;

        public void Dispose() => _listener.Dispose();
    }

    private IDeadLetters DeadLetterStorage() =>
        (
            Host.Services.GetService<IWolverineRuntime>()
            ?? throw new InvalidOperationException("The Host stores no deliveries.")
        )
            .Storage
            .DeadLetters;

    private StoredDeliveries? Deliveries() =>
        _deliveries ??= Host.Services.GetService<IWolverineRuntime>()?.Storage
            is IMessageDatabase store
            ? new StoredDeliveries(
                ConnectionString,
                store.SchemaName,
                [
                    .. ((IFeatureSchema)store).Objects.Select(table =>
                        (table.Identifier.Schema, table.Identifier.Name)
                    ),
                ]
            )
            : null;
}
