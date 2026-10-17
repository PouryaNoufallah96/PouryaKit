using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Persistence.EFCore;

namespace PouryaKit.Realtime;

// The connection string the listener listens over, read from the Host's final configuration when its container is
// built, and the ones the Units of Work store over: a push is written on its Unit of Work's own connection, and PostgreSQL
// hands a notification only to the listeners of the database it was written in, so a Unit of Work over another database
// would push to no client. The DbContexts are those AddPouryaKitEfCore records with the connection string they store
// over; one the application registered itself names none and is not compared. Two strings are compared as configured:
// two that differ fail, though they may name one database, and one connection string serves both. Nothing connects.
internal sealed class RealtimeConnectionCheck(RealtimeConnection realtime) : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        List<DbContextConnection> stored = [.. services.GetServices<DbContextConnection>()];
        var listened = configuration.GetConnectionString(realtime.Name);
        if (string.IsNullOrWhiteSpace(listened))
        {
            // A DbContext stored over the same name already has DbContextConnectionsCheck's line.
            if (!stored.Any(connection => string.Equals(connection.Name, realtime.Name, StringComparison.OrdinalIgnoreCase)))
            {
                yield return $"AddPouryaKitRealtime listens over the connection string {realtime.Name}, which the Host's configuration does not set (ConnectionStrings:{realtime.Name}), so the Host would not start. Set ConnectionStrings:{realtime.Name}, or name a connection the Host configures.";
            }

            yield break;
        }

        foreach (var connection in stored)
        {
            // A missing or blank one is DbContextConnectionsCheck's line.
            if (
                connection.Name is { } name
                && !string.Equals(name, realtime.Name, StringComparison.OrdinalIgnoreCase)
                && configuration.GetConnectionString(name) is { } other
                && !string.IsNullOrWhiteSpace(other)
                && other != listened
            )
            {
                yield return $"{connection.DbContext.Name} is stored over the connection string {name}, but AddPouryaKitRealtime listens over the connection string {realtime.Name}, so what its Notification Handlers push is written on another connection than the one the listener holds, and PostgreSQL hands a notification only to the listeners of its own database. Use one connection string for Realtime and every Module's DbContext.";
            }
        }
    }
}
