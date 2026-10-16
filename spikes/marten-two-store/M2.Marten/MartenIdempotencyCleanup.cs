using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using PouryaKit.Persistence.EFCore;

namespace M2.Marten;

/// <summary>A Stream Module's schema, recorded once per Module by its Marten registration.</summary>
internal sealed record MartenModuleSchema(string Schema);

// IdempotencyKeyCleanup's loop over the Stream Modules' key tables, under the same options: no timer when the retention
// is infinite, otherwise every interval each table loses its keys reserved before now minus the retention, a batch per
// statement on a connection of its own, outside every Unit of Work.
internal sealed class MartenIdempotencyCleanup(
    IEnumerable<MartenModuleSchema> schemas,
    MartenConnection connection,
    IOptions<PouryaKitIdempotencyOptions> options,
    TimeProvider timeProvider
) : BackgroundService
{
    private PeriodicTimer? _timer;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.Retention != Timeout.InfiniteTimeSpan)
        {
            _timer = new PeriodicTimer(options.Value.CleanupInterval, timeProvider);
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_timer is null)
        {
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(connection.ConnectionString);
        while (await _timer.WaitForNextTickAsync(stoppingToken))
        {
            var cutoff = timeProvider.GetUtcNow() - options.Value.Retention;
            foreach (var schema in schemas.Select(module => module.Schema).Distinct())
            {
                var table = $"{schema}.{MartenTables.IdempotencyKeys}";
                int removed;
                do
                {
                    await using var delete = dataSource.CreateCommand(
                        $"delete from {table} where ctid in (select ctid from {table} where reserved_at < $1 limit $2)"
                    );
                    delete.Parameters.AddWithValue(cutoff);
                    delete.Parameters.AddWithValue(options.Value.BatchSize);
                    removed = await delete.ExecuteNonQueryAsync(stoppingToken);
                } while (removed == options.Value.BatchSize);
            }
        }
    }

    public override void Dispose()
    {
        _timer?.Dispose();
        base.Dispose();
    }
}
