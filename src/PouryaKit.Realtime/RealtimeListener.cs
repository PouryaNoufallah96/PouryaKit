using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace PouryaKit.Realtime;

// One per instance: a dedicated connection, outside every pool and never multiplexed, since a pooled one is reset when
// returned and a multiplexed one holds no session to listen in. KeepAlive makes its wait send a query now and then, so a
// connection the network dropped fails here rather than wait for ever. The Host starts listening before it serves, so a
// client that is connected receives every push committed after its connected event. Once the connection is lost, pushes
// committed until the listener listens again reach no client of this instance: so it reconnects, listens, and only then
// sends every connected client a gap, after which a client's re-read misses nothing.
internal sealed partial class RealtimeListener(
    RealtimeConnection realtime,
    IConfiguration configuration,
    RealtimeClients clients,
    IOptions<JsonOptions> json,
    ILogger<RealtimeListener> logger
) : BackgroundService
{
    // The application name the listener's session shows in pg_stat_activity, unless the connection string names one.
    public const string ApplicationName = "pouryakit-realtime";

    private const int KeepAliveSeconds = 30;

    private static readonly TimeSpan LongestRetryDelay = TimeSpan.FromSeconds(30);

    private string? _connectionString;
    private NpgsqlConnection? _listening;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        NpgsqlConnectionStringBuilder settings = new(
            configuration.GetConnectionString(realtime.Name)
        )
        {
            Pooling = false,
            Multiplexing = false,
        };
        settings.KeepAlive = settings.KeepAlive > 0 ? settings.KeepAlive : KeepAliveSeconds;
        settings.ApplicationName ??= ApplicationName;
        _connectionString = settings.ConnectionString;
        _listening = await ListenAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listening = _listening!;
        try
        {
            while (true)
            {
                try
                {
                    await listening.WaitAsync(stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    LogLost(logger, exception);
                    await listening.DisposeAsync();
                    listening = await ReconnectAsync(stoppingToken);
                    clients.SendGap();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            await listening.DisposeAsync();
        }
    }

    private async Task<NpgsqlConnection> ReconnectAsync(CancellationToken stoppingToken)
    {
        for (
            var delay = TimeSpan.FromSeconds(1);
            ;
            delay = delay * 2 < LongestRetryDelay ? delay * 2 : LongestRetryDelay
        )
        {
            try
            {
                return await ListenAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                LogRetrying(logger, exception, delay);
                await Task.Delay(delay, stoppingToken);
            }
        }
    }

    private async Task<NpgsqlConnection> ListenAsync(CancellationToken cancellationToken)
    {
        NpgsqlConnection listening = new(_connectionString);
        listening.Notification += (_, notification) =>
        {
            if (
                RealtimeNotification.Read(notification.Payload, json.Value.SerializerOptions) is
                { } read
            )
            {
                clients.Push(read);
            }
            else
            {
                LogUnread(logger, notification.PID);
            }
        };
        try
        {
            await listening.OpenAsync(cancellationToken);
            await using NpgsqlCommand listen = new(
                $"LISTEN {RealtimeNotification.PostgresChannel}",
                listening
            );
            await listen.ExecuteNonQueryAsync(cancellationToken);
            return listening;
        }
        catch
        {
            await listening.DisposeAsync();
            throw;
        }
    }

    [LoggerMessage(
        EventName = "ListenerLost",
        Level = LogLevel.Warning,
        Message = "The realtime listener lost its connection; it reconnects, then sends every connected client a gap."
    )]
    private static partial void LogLost(ILogger logger, Exception exception);

    [LoggerMessage(
        EventName = "ListenerRetrying",
        Level = LogLevel.Warning,
        Message = "The realtime listener could not listen again; it retries in {Delay}."
    )]
    private static partial void LogRetrying(ILogger logger, Exception exception, TimeSpan delay);

    [LoggerMessage(
        EventName = "NotificationUnread",
        Level = LogLevel.Warning,
        Message = "A notification on the realtime channel from the session {Pid} is not a realtime push; it was ignored."
    )]
    private static partial void LogUnread(ILogger logger, int pid);
}
