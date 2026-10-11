using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PouryaKit.Persistence.EFCore;

// Removes the expired idempotency keys of every Module in the Host, each in a scope of its own, outside every request
// and Unit of Work. Each batch is one statement committed on its own, so a cleanup never holds many row locks; a key
// whose Operation has not committed is invisible to it. A Module whose cleanup fails is logged, and the next tick runs.
internal sealed partial class IdempotencyKeyCleanup(
    IEnumerable<DbContextConnection> dbContexts,
    IServiceScopeFactory scopes,
    IOptions<PouryaKitIdempotencyOptions> options,
    TimeProvider timeProvider,
    ILogger<IdempotencyKeyCleanup> logger
) : BackgroundService
{
    // What PeriodicTimer accepts, and a retention whose cutoff stays far from DateTimeOffset.MinValue.
    internal static readonly TimeSpan MinCleanupInterval = TimeSpan.FromMilliseconds(1);
    internal static readonly TimeSpan MaxCleanupInterval = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
    internal static readonly TimeSpan MaxRetention = TimeSpan.FromDays(36500);

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

        while (await _timer.WaitForNextTickAsync(stoppingToken))
        {
            // Each DbContext AddPouryaKitEfCore made a Unit of Work, recorded once.
            foreach (var dbContextType in dbContexts.Select(connection => connection.DbContext))
            {
                try
                {
                    await RemoveExpiredAsync(dbContextType, stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    LogFailed(logger, exception, dbContextType.Name);
                }
            }
        }
    }

    public override void Dispose()
    {
        _timer?.Dispose();
        base.Dispose();
    }

    private async Task RemoveExpiredAsync(Type dbContextType, CancellationToken stoppingToken)
    {
        var batchSize = options.Value.BatchSize;
        var cutoff = timeProvider.GetUtcNow() - options.Value.Retention;
        await using var scope = scopes.CreateAsyncScope();
        var dbContext = (DbContext)scope.ServiceProvider.GetRequiredService(dbContextType);
        var database = dbContext.Database;
        var table = IdempotencyTable.In(dbContext);
        // A Module with no [Idempotent] command has no table; looked up first, so no failed statement is logged.
        if (!await database.SqlQueryRaw<bool>("select to_regclass({0}) is not null as \"Value\"", table).SingleAsync(stoppingToken))
        {
            return;
        }

        var delete = $"delete from {table} where ctid in (select ctid from {table} where reserved_at < {{0}} limit {{1}})";
        var (removed, batches, batch) = (0, 0, 0);
        do
        {
            batch = await database.ExecuteSqlRawAsync(delete, [cutoff, batchSize], stoppingToken);
            removed += batch;
            batches++;
        } while (batch == batchSize);

        if (removed > 0)
        {
            LogRemoved(logger, removed, table, cutoff, batches);
        }
    }

    [LoggerMessage(
        EventName = "IdempotencyKeysRemoved",
        Level = LogLevel.Information,
        Message = "Removed {Count} idempotency keys reserved before {Cutoff:O} from {Table} in {Batches} batches"
    )]
    private static partial void LogRemoved(ILogger logger, int count, string table, DateTimeOffset cutoff, int batches);

    [LoggerMessage(
        EventName = "IdempotencyKeyCleanupFailed",
        Level = LogLevel.Error,
        Message = "Removing the expired idempotency keys of {DbContext} failed; the next cleanup tries again"
    )]
    private static partial void LogFailed(ILogger logger, Exception exception, string dbContext);

    // Once for the Host, however many Modules register the store.
    internal sealed class Validation : IValidateOptions<PouryaKitIdempotencyOptions>
    {
        public ValidateOptionsResult Validate(string? name, PouryaKitIdempotencyOptions options)
        {
            List<string> failures = [];
            if ((options.Retention <= TimeSpan.Zero || options.Retention > MaxRetention) && options.Retention != Timeout.InfiniteTimeSpan)
            {
                failures.Add(
                    $"The idempotency key Retention is {options.Retention}; it must be positive and at most {MaxRetention}, or Timeout.InfiniteTimeSpan to keep every key."
                );
            }

            if (options.CleanupInterval < MinCleanupInterval || options.CleanupInterval > MaxCleanupInterval)
            {
                failures.Add(
                    $"The idempotency key CleanupInterval is {options.CleanupInterval}; it must be from {MinCleanupInterval} to {MaxCleanupInterval}."
                );
            }

            if (options.BatchSize < 1)
            {
                failures.Add($"The idempotency key BatchSize is {options.BatchSize}; it must be at least 1.");
            }

            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }
    }
}
