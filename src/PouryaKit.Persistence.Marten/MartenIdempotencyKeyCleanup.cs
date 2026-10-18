using Marten;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PouryaKit.Cqrs;
using Weasel.Postgresql;

namespace PouryaKit.Persistence.Marten;

// Removes the expired idempotency keys of every Stream Module in the Host, as the EF cleanup does for every DbContext,
// under the same options: no timer when the retention is infinite; otherwise, every interval, each Module's table loses
// the keys reserved before now minus the retention, a batch per statement committed on its own on the store's data
// source, outside every Unit of Work, so a key whose Operation has not committed is invisible to it. A Module whose
// cleanup fails is logged, and the next tick runs.
internal sealed partial class MartenIdempotencyKeyCleanup(
    StreamModules streams,
    IDocumentStore store,
    IOptions<PouryaKitIdempotencyOptions> options,
    TimeProvider timeProvider,
    ILogger<MartenIdempotencyKeyCleanup> logger
) : BackgroundService
{
    private PeriodicTimer? _timer;

    // The timer starts with the Host, not in ExecuteAsync, which .NET 10 runs on the thread pool: a tick due after the
    // Host started is never missed.
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

        var dataSource = ((PostgresqlDatabase)store.Storage.Database).DataSource;
        while (await _timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var schema in streams.Schemas)
            {
                var table = $"\"{schema.Replace("\"", "\"\"", StringComparison.Ordinal)}\".{IdempotencyStatements.Table}";
                try
                {
                    var (cutoff, batchSize) = (timeProvider.GetUtcNow() - options.Value.Retention, options.Value.BatchSize);
                    int batch;
                    do
                    {
                        await using var connection = await dataSource.OpenConnectionAsync(stoppingToken);
                        await using var delete = Statements.Command(IdempotencyStatements.RemoveExpired(table), [cutoff, batchSize], connection);
                        batch = await delete.ExecuteNonQueryAsync(stoppingToken);
                    } while (batch == batchSize);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    LogFailed(logger, exception, table);
                }
            }
        }
    }

    public override void Dispose()
    {
        _timer?.Dispose();
        base.Dispose();
    }

    [LoggerMessage(
        EventName = "IdempotencyKeyCleanupFailed",
        Level = LogLevel.Error,
        Message = "Removing the expired idempotency keys of {Table} failed; the next cleanup tries again"
    )]
    private static partial void LogFailed(ILogger logger, Exception exception, string table);
}
