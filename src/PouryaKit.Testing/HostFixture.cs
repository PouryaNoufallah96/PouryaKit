using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using PouryaKit.Modularity;
using Respawn;
using Respawn.Graph;
using Testcontainers.PostgreSql;

namespace PouryaKit.Testing;

/// <summary>
/// Hosts the application against a PostgreSQL started in a container: it builds the Host with
/// <see cref="CreateHost"/>, starts it, and applies every Module's migrations. Share one per test assembly and derive
/// test classes from <see cref="HostTest{TFixture}"/>, which resets the data before each test.
/// </summary>
public abstract class HostFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.6").Build();
    private IHost? _host;
    private Respawner? _respawner;

    /// <summary>The connection string of the test database.</summary>
    public string ConnectionString => _postgres.GetConnectionString();

    /// <summary>The started Host.</summary>
    public IHost Host => _host ?? throw new InvalidOperationException("The Host starts in InitializeAsync.");

    /// <summary>
    /// Builds the application's Host, not yet started, with <paramref name="connectionString"/> as the connection
    /// string its Modules use.
    /// </summary>
    protected abstract IHost CreateHost(string connectionString);

    /// <summary>The Gateway of <typeparamref name="TModule"/>, for sending Operations as the application does.</summary>
    public IGatewayProcessor<TModule> Gateway<TModule>()
        where TModule : IModuleDefinition => Host.Services.GetRequiredService<IGatewayProcessor<TModule>>();

    /// <summary>Deletes every row except the migrations history, in every schema.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        _respawner ??= await Respawner.CreateAsync(
            connection,
            new RespawnerOptions
            {
                DbAdapter = DbAdapter.Postgres,
                TablesToIgnore = [new Table(HistoryRepository.DefaultTableName)],
            }
        );
        await _respawner.ResetAsync(connection);
    }

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        _host = CreateHost(ConnectionString);
        await _host.StartAsync();

        // Every DbContext registered with AddDbContext is also registered as one non-generic DbContextOptions.
        await using var scope = _host.Services.CreateAsyncScope();
        foreach (var options in scope.ServiceProvider.GetServices<DbContextOptions>())
        {
            var dbContext = (DbContext)scope.ServiceProvider.GetRequiredService(options.ContextType);
            await dbContext.Database.MigrateAsync();
        }
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
}
