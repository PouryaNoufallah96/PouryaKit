using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Persistence.EFCore;

namespace PouryaKit.Realtime;

// The connection string the listener listens over, read from the Host's final configuration when its container is
// built, and the connection each Unit of Work stores over: a push is written on its Unit of Work's own connection, and
// PostgreSQL hands a notification only to the listeners of the database it was written in, so a Unit of Work over
// another database would push to no client. The DbContexts are every one AddPouryaKitEfCore made a Unit of Work, and
// each one's side is the connection EF Core gives the DbContext itself, created in the check's own scope, whatever
// configured it, a ConfigureDbContext after UsePouryaKitPostgres included, as the message store's check reads it. The
// two are compared on the settings Npgsql reads from them, the password aside, which a data source leaves out of its
// own: two that set anything apart fail, though they may name one database, and one connection string serves both.
// Nothing connects.
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

        // AddDbContext registers each DbContext scoped.
        using var scope = services.CreateScope();
        foreach (var connection in stored)
        {
            // A missing or blank named one is DbContextConnectionsCheck's line.
            if (connection.Name is { } name && string.IsNullOrWhiteSpace(configuration.GetConnectionString(name)))
            {
                continue;
            }

            // Constructing the DbContext builds its connection, which is not opened; what EF Core refuses there fails the
            // build as EF Core's own exception.
            var context = (DbContext)scope.ServiceProvider.GetRequiredService(connection.DbContext);
            if (!SameSettings(listened, context.Database.GetDbConnection().ConnectionString ?? ""))
            {
                var registered = connection.Name is { } named ? $"stored over the connection string {named}" : "which the application registered itself";
                yield return $"EF Core connects {connection.DbContext.Name}, {registered}, with other settings than the connection string {realtime.Name} that AddPouryaKitRealtime listens over, so what its Notification Handlers push may be written in another database than the listener's, and PostgreSQL hands a notification only to the listeners of its own database. Use one connection string for Realtime and every Module's DbContext.";
            }
        }
    }

    private static bool SameSettings(string listened, string stored) =>
        new NpgsqlConnectionStringBuilder(listened) { Password = null }.EquivalentTo(new NpgsqlConnectionStringBuilder(stored) { Password = null });
}
