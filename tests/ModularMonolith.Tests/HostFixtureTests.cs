using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

// The fixture every test against the hosted application derives from, cleaning up after a Host that fails to stop.
public sealed class HostFixtureTests
{
    [Fact]
    public async Task A_host_that_fails_to_stop_is_still_disposed_asynchronously_and_the_database_container_with_it()
    {
        var fixture = new StopFailingFixture();
        await fixture.InitializeAsync();
        var database = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Pooling = false,
        }.ConnectionString;
        await OpenAsync(database);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.DisposeAsync().AsTask()
        );

        Assert.Equal(StopFailingHost.Failure, thrown.Message);
        Assert.Equal([nameof(IAsyncDisposable.DisposeAsync)], fixture.Created.Disposals);
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => OpenAsync(database));
    }

    // A Module before its first migration: the fixture starts its Host and applies nothing for it.
    [Fact]
    public async Task A_host_whose_dbcontext_has_no_migration_yet_starts_with_nothing_applied()
    {
        var fixture = new UnmigratedFixture();
        try
        {
            await fixture.InitializeAsync();

            Assert.True(
                fixture
                    .Host.Services.GetRequiredService<IHostApplicationLifetime>()
                    .ApplicationStarted.IsCancellationRequested
            );
            await using var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var drafts = new NpgsqlCommand(
                "select to_regclass('\"Drafts\"') is null",
                connection
            );
            Assert.Equal(
                true,
                await drafts.ExecuteScalarAsync(TestContext.Current.CancellationToken)
            );
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task OpenAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
    }

    private sealed class StopFailingFixture : HostFixture
    {
        public StopFailingHost Created { get; } = new();

        protected override IHost CreateHost(string connectionString) => Created;
    }

    private sealed class UnmigratedFixture : HostFixture
    {
        protected override IHost CreateHost(string connectionString)
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Services.AddDbContext<UnmigratedDbContext>(options =>
                options.UseNpgsql(connectionString)
            );
            return builder.Build();
        }
    }

    // A model with a table and no migration that creates it.
    private sealed class UnmigratedDbContext(DbContextOptions<UnmigratedDbContext> options)
        : DbContext(options)
    {
        public DbSet<Draft> Drafts => Set<Draft>();
    }

    private sealed record Draft(Guid Id);

    // A Host with no services, as a WebApplication disposable both ways, whose stop fails; it records how it is disposed.
    private sealed class StopFailingHost : IHost, IAsyncDisposable
    {
        public const string Failure = "The Host failed to stop.";

        private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();

        public IServiceProvider Services => _services;

        public List<string> Disposals { get; } = [];

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException(Failure));

        public void Dispose()
        {
            Disposals.Add(nameof(Dispose));
            _services.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Disposals.Add(nameof(DisposeAsync));
            return _services.DisposeAsync();
        }
    }
}
