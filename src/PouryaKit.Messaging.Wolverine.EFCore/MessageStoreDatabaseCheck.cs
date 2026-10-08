using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Persistence.EFCore;

namespace PouryaKit.Messaging.Wolverine.EFCore;

// Wolverine writes each envelope on the Unit of Work's own connection and transaction, in the store's schema, so a DbContext
// on another database than the store's writes its Outbox there: the Operation fails when that database holds no store
// tables, and commits where no delivery reads it when it holds a copy of them. The store's side is the connection string
// Wolverine was given; each DbContext's is read from the Host's final configuration, as EF Core resolves it. They are
// compared on the hosts, ports and database they name: nothing connects or resolves a host name, so localhost and
// 127.0.0.1 differ. A string that names no database names its user's, as Npgsql and PostgreSQL read it; one that names
// neither leaves the database to the client's user, so its database is unknown and not compared.
internal sealed class MessageStoreDatabaseCheck(string storeConnectionName, string storeConnectionString) : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        if (string.IsNullOrWhiteSpace(storeConnectionString))
        {
            yield break;
        }

        var configuration = services.GetRequiredService<IConfiguration>();
        var store = PostgresDatabase.Of(storeConnectionString);
        foreach (var connection in services.GetServices<DbContextConnection>())
        {
            // A missing or blank one is PostgresConnectionsCheck's line.
            if (
                configuration.GetConnectionString(connection.Name) is { } named
                && !string.IsNullOrWhiteSpace(named)
                && PostgresDatabase.Of(named) is var database
                && !database.Matches(store)
            )
            {
                yield return $"UsePostgres stores {connection.DbContext.Name} over the connection string {connection.Name}, the database {database}, but UsePouryaKitWolverine keeps the message store over the connection string {storeConnectionName}, the database {store}, so its Unit of Work would write its Outbox to its own database, where the store never delivers it. Use one connection string for the message store and every Module's DbContext.";
            }
        }
    }

    // A database is its server's hosts, each with its port, Npgsql's default when the string names none, and its name, null
    // when unknown.
    private sealed record PostgresDatabase(string Hosts, string? Name)
    {
        public static PostgresDatabase Of(string connectionString)
        {
            var settings = new NpgsqlConnectionStringBuilder(connectionString);
            var hosts = (settings.Host ?? "")
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(host => (HasPort(host) ? host : $"{host}:{settings.Port}").ToLowerInvariant())
                .Order(StringComparer.Ordinal);
            return new(string.Join(',', hosts), settings.Database ?? settings.Username);
        }

        public bool Matches(PostgresDatabase other) => Hosts == other.Hosts && (Name is null || other.Name is null || Name == other.Name);

        public override string ToString() => Name is null ? Hosts : $"{Hosts}/{Name}";

        // Npgsql's reading of a host (NpgsqlConnectionStringBuilder.TrySplitHostPort): its last colon starts a port unless
        // the host is a bare IPv6 address.
        private static bool HasPort(string host)
        {
            var colon = host.LastIndexOf(':');
            var previous = colon == -1 ? -1 : host.AsSpan(0, colon).LastIndexOf(':');
            var bracket = host.LastIndexOf(']');
            return colon != -1 && (previous == -1 || (colon > bracket && previous < bracket));
        }
    }
}
